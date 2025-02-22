﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed;
using BuildXL.Cache.ContentStore.Distributed.Blob;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Distributed.NuCache.EventStreaming;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Auth;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Timers;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Utilities.Core;

namespace BuildXL.Cache.BlobLifetimeManager.Library
{
    public class BlobLifetimeManager
    {
        private static readonly Tracer Tracer = new(nameof(BlobLifetimeManager));

        public async Task RunAsync(
            OperationContext context,
            BlobQuotaKeeperConfig config,
            IAbsFileSystem fileSystem,
            IBlobCacheSecretsProvider secretsProvider,
            IReadOnlyList<BlobCacheStorageAccountName> accountNames,
            IClock clock,
            string runId,
            int contentDegreeOfParallelism,
            int fingerprintDegreeOfParallelism,
            bool dryRun)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
            context = context.WithCancellationToken(cts.Token);

            var shardingScheme = new ShardingScheme(ShardingAlgorithm.JumpHash, accountNames);
            var (metadataMatrix, contentMatrix) = ShardedBlobCacheTopology.GenerateMatrix(shardingScheme);

            var maxSizes = config.Namespaces.ToDictionary(
                c => new BlobNamespaceId(c.Universe, c.Namespace),
                c => (long)(c.MaxSizeGb * 1024 * 1024 * 1024));

            var namespaces = maxSizes.Keys.ToList();

            var topologies = namespaces.ToDictionary(
                n => n,
                n => (IBlobCacheTopology)new ShardedBlobCacheTopology(
                    new ShardedBlobCacheTopology.Configuration(
                        shardingScheme,
                        secretsProvider,
                        n.Universe,
                        n.Namespace)));

            // Using the 0th shard of the cache, so that checkpoint data is preserved between re-sharding.
            // However, the container needs to be differentiated between reshardings.
            var checkpointContainerName = $"checkpoints-{metadataMatrix}";
            var checkpointManagerStorageAccount = ShardingScheme.SortAccounts(accountNames).First();
            Contract.Assert(checkpointManagerStorageAccount is not null);

            var checkpointManagerStorageCreds = await secretsProvider.RetrieveBlobCredentialsAsync(context, checkpointManagerStorageAccount);

            var machineLocation = new MachineLocation(runId);

            await RunWithLeaseAsync(
                    context,
                    checkpointManagerStorageCreds,
                    checkpointContainerName,
                    machineLocation,
                    cts,
                    clock,
                    async () =>
                    {
                        await LogConfigAndAccountDifferencesAsync(context, secretsProvider, accountNames, config, metadataMatrix, contentMatrix);

                        using var temp = new DisposableDirectory(fileSystem);

                        var rootPath = temp.Path / "LifetimeDatabase";
                        var dbConfig = new RocksDbLifetimeDatabase.Configuration
                        {
                            DatabasePath = rootPath.Path,
                            LruEnumerationPercentileStep = config.LruEnumerationPercentileStep,
                            LruEnumerationBatchSize = config.LruEnumerationBatchSize,
                            BlobNamespaceIds = namespaces,
                        };

                        var checkpointable = new RocksDbLifetimeDatabase.CheckpointableLifetimeDatabase(
                            dbConfig,
                            clock);

                        var registry = new AzureBlobStorageCheckpointRegistry(
                            new AzureBlobStorageCheckpointRegistryConfiguration
                            {
                                Storage = new AzureBlobStorageCheckpointRegistryConfiguration.StorageSettings(
                                    Credentials: checkpointManagerStorageCreds,
                                    ContainerName: checkpointContainerName)
                            });

                        var centralStorage = new BlobCentralStorage(new BlobCentralStoreConfiguration(
                            checkpointManagerStorageCreds,
                            containerName: checkpointContainerName,
                            checkpointsKey: "lifetime-manager"
                            ));

                        var checkpointManager = new CheckpointManager(
                            checkpointable,
                            registry,
                            centralStorage,
                            new CheckpointManagerConfiguration(
                                WorkingDirectory: temp.Path / "CheckpointManager",
                                PrimaryMachineLocation: machineLocation)
                            {
                                // We don't want to restore checkpoints on a loop.
                                RestoreCheckpoints = false,
                            },
                            new CounterCollection<ContentLocationStoreCounters>(),
                            clock);

                        await checkpointManager.StartupAsync(context).ThrowIfFailure();
                        var checkpointStateResult = await registry.GetCheckpointStateAsync(context).ThrowIfFailure();

                        RocksDbLifetimeDatabase db;
                        var firstRun = string.IsNullOrEmpty(checkpointStateResult.Value?.CheckpointId);
                        if (!firstRun)
                        {
                            await checkpointManager.RestoreCheckpointAsync(context, checkpointStateResult.Value!).ThrowIfFailure();
                            db = checkpointable.GetDatabase();
                        }
                        else
                        {
                            db = await LifetimeDatabaseCreator.CreateAsync(
                                context,
                                dbConfig,
                                clock,
                                contentDegreeOfParallelism,
                                fingerprintDegreeOfParallelism,
                                namespaceId => topologies[namespaceId]).ThrowIfFailureAsync();

                            checkpointable.Database = db;
                        }

                        using (db)
                        {
                            var accessors = namespaces.ToDictionary(
                                n => n,
                                db.GetAccessor);

                            if (!firstRun)
                            {
                                // Only get updates from Azure if the database already existed
                                var updater = new LifetimeDatabaseUpdater(topologies, accessors, clock, fingerprintDegreeOfParallelism);
                                AzureStorageChangeFeedEventDispatcher dispatcher =
                                    CreateDispatcher(secretsProvider, accountNames, metadataMatrix, contentMatrix, db, updater, clock, checkpointManager, config.ChangeFeedPageSize);

                                await dispatcher.ConsumeNewChangesAsync(context, config.CheckpointCreationInterval).ThrowIfFailure();
                            }

                            // TODO: consider doing this in parallel, although it could be argued that if each of these calls
                            // is making full use of resources, it can be better to just do this sequentially.
                            foreach (var namespaceId in namespaces)
                            {
                                context.Token.ThrowIfCancellationRequested();

                                var accessor = accessors[namespaceId];
                                var quotaKeeper = new BlobQuotaKeeper(accessor, topologies[namespaceId], config.LastAccessTimeDeletionThreshold, clock);
                                _ = await quotaKeeper.EnsureUnderQuota(
                                    context,
                                    maxSizes[namespaceId],
                                    dryRun,
                                    contentDegreeOfParallelism,
                                    fingerprintDegreeOfParallelism,
                                    checkpointManager,
                                    config.CheckpointCreationInterval);
                            }

                            if (!dryRun)
                            {
                                context.Token.ThrowIfCancellationRequested();

                                await checkpointManager.CreateCheckpointAsync(
                                    context,
                                    new EventSequencePoint(clock.UtcNow),
                                    maxEventProcessingDelay: null)
                                    .ThrowIfFailure();
                            }
                        }
                    });
        }

        protected virtual AzureStorageChangeFeedEventDispatcher CreateDispatcher(
            IBlobCacheSecretsProvider secretsProvider,
            IReadOnlyList<BlobCacheStorageAccountName> accountNames,
            string metadataMatrix,
            string contentMatrix,
            RocksDbLifetimeDatabase db,
            LifetimeDatabaseUpdater updater,
            IClock clock,
            CheckpointManager checkpointManager,
            int? changeFeedPageSize)
        {
            return new AzureStorageChangeFeedEventDispatcher(secretsProvider, accountNames, updater, checkpointManager, db, clock, metadataMatrix, contentMatrix, changeFeedPageSize);
        }

        private static async Task RunWithLeaseAsync(
            OperationContext context,
            IAzureStorageCredentials checkpointManagerStorageAccount,
            string checkpointContainerName,
            MachineLocation machineLocation,
            CancellationTokenSource cts,
            IClock clock,
            Func<Task> run)
        {
            var masterSelection = new AzureBlobStorageMasterElectionMechanism(
                new AzureBlobStorageMasterElectionMechanismConfiguration()
                {
                    Storage = new AzureBlobStorageMasterElectionMechanismConfiguration.StorageSettings(
                            checkpointManagerStorageAccount,
                            ContainerName: checkpointContainerName),
                    ReleaseLeaseOnShutdown = true,
                },
                machineLocation,
                clock);

            await masterSelection.StartupAsync(context).ThrowIfFailure();
            var state = await masterSelection.GetRoleAsync(context).ThrowIfFailureAsync();

            if (state.Role != Role.Master)
            {
                throw new InvalidOperationException(
                    $"Failed to get lease on the blob cache. This indicates another GC run is already happening. The cache is currently leased to: {state.Master.Path}");
            }

            try
            {
                using var timer = new IntervalTimer(async () =>
                    {
                        var stateResult = await masterSelection.GetRoleAsync(context);
                        if (!stateResult.Succeeded || stateResult.Value.Role != Role.Master)
                        {
                            Tracer.Error(
                                context,
                                $"Failed to renew lease on the blob cache. Aborting since this could lead to multiple GC runs running at the same time.");

                            cts.Cancel();
                        }
                    },
                    period: TimeSpan.FromMinutes(5),
                    dueTime: TimeSpan.FromMinutes(5));

                await run();
            }
            finally
            {
                await masterSelection.ShutdownAsync(context).ThrowIfFailure();
            }
        }

        private static async Task LogConfigAndAccountDifferencesAsync(
            OperationContext context,
            IBlobCacheSecretsProvider secretsProvider,
            IReadOnlyList<BlobCacheStorageAccountName> accounts,
            BlobQuotaKeeperConfig config,
            string metadataMatrix,
            string contentMatrix)
        {
            var configuredNamespaces = config.Namespaces.Select(config => (config.Universe, config.Namespace)).ToHashSet();
            var enumeratedNamespaces = new HashSet<(string Universe, string Namespace)>();

            // Start by enumerating all accounts and their containers, attempting to parse their namespaces.
            foreach (var account in accounts)
            {
                var cred = await secretsProvider.RetrieveBlobCredentialsAsync(context, account);
                var client = cred.CreateBlobServiceClient();

                await foreach (var container in client.GetBlobContainersAsync())
                {
                    try
                    {
                        var name = BlobCacheContainerName.Parse(container.Name);

                        if (!configuredNamespaces.Contains((name.Universe, name.Namespace)))
                        {
                            Tracer.Warning(context, $"Container {container.Name} in account {client.AccountName} has " +
                                $"(Universe=[{name.Universe}], Namespace=[{name.Namespace}]), which is not listed in the configuration.");
                        }
                        else if (name.Purpose == BlobCacheContainerPurpose.Metadata)
                        {
                            if (name.Matrix != metadataMatrix)
                            {
                                Tracer.Warning(context, $"Container {container.Name} in account {client.AccountName} has Matrix=[{name.Matrix}], which does" +
                                    $"not match current matrix=[{metadataMatrix}]. Resharding is likely to have occurred.");

                                continue;
                            }
                        }
                        else if (name.Purpose == BlobCacheContainerPurpose.Content)
                        {
                            if (name.Matrix != contentMatrix)
                            {
                                Tracer.Warning(context, $"Container {container.Name} in account {client.AccountName} has Matrix=[{name.Matrix}], which does" +
                                    $"not match current matrix=[{contentMatrix}]. Resharding is likely to have occurred.");

                                continue;
                            }
                        }

                        enumeratedNamespaces.Add((name.Universe, name.Namespace));
                    }
                    catch (FormatException)
                    {
                        Tracer.Warning(context, $"Failed to parse container name {container.Name} in account {client.AccountName}");
                    }
                }
            }

            // Now find all configured namespaces with no matching containers.
            foreach (var (universe, @namespace) in configuredNamespaces.Except(enumeratedNamespaces))
            {
                Tracer.Warning(context, $"(Universe=[{universe}], Namespace=[{@namespace}]) was found in configuration but no matching containers were found.");
            }
        }
    }
}
