// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Linq;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using BuildXL.Cache.ContentStore.Distributed.Blob;
using BuildXL.Cache.ContentStore.Distributed.NuCache.ClusterStateManagement;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Auth;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;

#nullable enable

namespace BuildXL.Cache.ContentStore.Distributed.NuCache;

public record BlobClusterStateStorageConfiguration
{
    public record StorageSettings(IAzureStorageCredentials Credentials, string ContainerName = "checkpoints", string FolderName = "clusterState")
        : AzureBlobStorageFolder.Configuration(Credentials, ContainerName, FolderName);

    public required StorageSettings Storage { get; init; }

    public BlobFolderStorageConfiguration BlobFolderStorageConfiguration { get; set; } = new BlobFolderStorageConfiguration();

    public string FileName { get; set; } = "clusterState.json";

    public ClusterStateRecomputeConfiguration RecomputeConfiguration { get; set; } = new ClusterStateRecomputeConfiguration();
}

public class BlobClusterStateStorage : StartupShutdownComponentBase, IClusterStateStorage
{
    protected override Tracer Tracer { get; } = new Tracer(nameof(BlobClusterStateStorage));

    private readonly BlobClusterStateStorageConfiguration _configuration;
    private readonly IClock _clock;

    private readonly BlobStorageClientAdapter _storageClientAdapter;
    private readonly BlobClient _client;

    public BlobClusterStateStorage(
        BlobClusterStateStorageConfiguration configuration,
        IClock? clock = null)
    {
        _configuration = configuration;
        _clock = clock ?? SystemClock.Instance;

        _storageClientAdapter = new BlobStorageClientAdapter(Tracer, _configuration.BlobFolderStorageConfiguration);

        var azureBlobStorageFolder = _configuration.Storage.Create();
        _client = azureBlobStorageFolder.GetBlobClient(new BlobPath(_configuration.FileName, relative: true));
    }

    protected override async Task<BoolResult> StartupComponentAsync(OperationContext context)
    {
        await _storageClientAdapter.EnsureContainerExists(context, _client.GetParentBlobContainerClient()).ThrowIfFailureAsync();
        return BoolResult.Success;
    }

    public Task<Result<IClusterStateStorage.RegisterMachineOutput>> RegisterMachinesAsync(OperationContext context, IClusterStateStorage.RegisterMachineInput request)
    {
        return context.PerformOperationAsync(
            Tracer,
            async () =>
            {
                var (currentState, assignedMachineIds) = await _storageClientAdapter.ReadModifyWriteAsync<ClusterStateMachine, MachineId[]>(
                    context,
                    _client,
                    currentState => currentState.RegisterMany(_configuration.RecomputeConfiguration, request, nowUtc: _clock.UtcNow)).ThrowIfFailureAsync();

                var machineMappings = request.MachineLocations
                    .Zip(assignedMachineIds, (machineLocation, machineId) => new MachineMapping(machineId, machineLocation))
                    .ToArray();

                return Result.Success(new IClusterStateStorage.RegisterMachineOutput(currentState, machineMappings));
            },
            traceOperationStarted: false);
    }

    public Task<Result<IClusterStateStorage.HeartbeatOutput>> HeartbeatAsync(OperationContext context, IClusterStateStorage.HeartbeatInput request)
    {
        return context.PerformOperationAsync(
            Tracer,
            async () =>
            {
                var (currentState, priorMachineRecords) = await _storageClientAdapter.ReadModifyWriteAsync<ClusterStateMachine, MachineRecord[]>(
                    context,
                    _client,
                    currentState => currentState.HeartbeatMany(request, nowUtc: _clock.UtcNow)).ThrowIfFailureAsync<(ClusterStateMachine NextState, MachineRecord[] Result)>();

                return Result.Success(new IClusterStateStorage.HeartbeatOutput(currentState, priorMachineRecords));
            },
            traceOperationStarted: false);
    }

    public Task<Result<ClusterStateMachine>> ReadStateAsync(OperationContext context)
    {
        return context.PerformOperationAsync(
            Tracer,
            async () =>
            {
                var currentState = await _storageClientAdapter.ReadAsync<ClusterStateMachine>(context, _client)
                    .ThrowIfFailureAsync();

                return Result.Success(currentState);
            },
            traceOperationStarted: false);
    }

}
