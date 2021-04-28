// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

export const pkgs = [
    { id: "EnvDTE", version: "8.0.2" },
    { id: "EnvDTE80", version: "8.0.3" },
    { id: "Microsoft.VisualStudio.ComponentModelHost", version: "15.8.525" },
    { id: "Microsoft.VisualStudio.Composition", version: "14.2.19-pre" },
    { id: "Microsoft.VisualStudio.CoreUtility", version: "15.4.27004" },
    { id: "Microsoft.VisualStudio.ImageCatalog", version: "15.8.28010" },
    { id: "Microsoft.VisualStudio.Imaging.Interop.14.0.DesignTime", version: "14.3.26930" },
    { id: "Microsoft.VisualStudio.Imaging", version: "15.8.28010", dependentPackageIdsToSkip: ["Microsoft.VisualStudio.Utilities"] }, // Have to cut this dependency because it is 46 only and this package is 45 compatible
    { id: "Microsoft.VisualStudio.LanguageServer.Protocol", version: "16.3.57" },
    { id: "Microsoft.VisualStudio.OLE.Interop", version: "7.10.6071" },
    { id: "Microsoft.VisualStudio.ProjectAggregator", version: "8.0.50728" },
    { id: "Microsoft.VisualStudio.ProjectSystem", version: "14.1.127-pre" },
    { id: "Microsoft.VisualStudio.SDK.EmbedInteropTypes", version: "15.0.26" },
    { id: "Microsoft.VisualStudio.SDK.VsixSuppression", version: "14.1.15" },
    { id: "Microsoft.VisualStudio.Shell.14.0", version: "14.3.25407", dependentPackageIdsToSkip: ["*"] }, // Have cut dependencies due to qualifier mismatches
    { id: "Microsoft.VisualStudio.Shell.15.0", version: "15.8.28010", dependentPackageIdsToSkip: ["Microsoft.VisualStudio.Text.Data"] }, // Have to cut this dependency because it is 46 only and this package is 45 compatible
    { id: "Microsoft.VisualStudio.Shell.Framework", version: "15.8.28010", dependentPackageIdsToSkip: ["Microsoft.VisualStudio.Utilities"] }, // Have to cut this dependency because it is 46 only and this package is 45 compatible
    { id: "Microsoft.VisualStudio.Shell.Immutable.10.0", version: "15.0.25415" },
    { id: "Microsoft.VisualStudio.Shell.Immutable.11.0", version: "15.0.25415" },
    { id: "Microsoft.VisualStudio.Shell.Immutable.12.0", version: "15.0.25415" },
    { id: "Microsoft.VisualStudio.Shell.Immutable.14.0", version: "15.0.25405" },
    { id: "Microsoft.VisualStudio.Shell.Interop.10.0", version: "10.0.30320" },
    { id: "Microsoft.VisualStudio.Shell.Interop.11.0", version: "11.0.61031" },
    { id: "Microsoft.VisualStudio.Shell.Interop.12.0", version: "12.0.30111" },
    { id: "Microsoft.VisualStudio.Shell.Interop.14.0.DesignTime", version: "14.3.26929" },
    { id: "Microsoft.VisualStudio.Shell.Interop.15.3.DesignTime", version: "15.0.26929" },
    { id: "Microsoft.VisualStudio.Shell.Interop.15.6.DesignTime", version: "15.6.27413" },
    { id: "Microsoft.VisualStudio.Shell.Interop.8.0", version: "8.0.50728" },
    { id: "Microsoft.VisualStudio.Shell.Interop.9.0", version: "9.0.30730" },
    { id: "Microsoft.VisualStudio.Shell.Interop", version: "7.10.6072" },
    { id: "Microsoft.VisualStudio.Text.Data", version: "15.8.525" },
    { id: "Microsoft.VisualStudio.TextManager.Interop.10.0", version: "10.0.30320" },
    { id: "Microsoft.VisualStudio.TextManager.Interop.11.0", version: "11.0.61032" },
    { id: "Microsoft.VisualStudio.TextManager.Interop.12.0", version: "12.0.30112" },
    { id: "Microsoft.VisualStudio.TextManager.Interop.8.0", version: "8.0.50728" },
    { id: "Microsoft.VisualStudio.TextManager.Interop.9.0", version: "9.0.30730" },
    { id: "Microsoft.VisualStudio.TextManager.Interop", version: "7.10.6071" },
    { id: "Microsoft.VisualStudio.Threading", version: "15.8.168" },
    { id: "Microsoft.VisualStudio.Utilities", version: "15.8.28010" },
    { id: "Microsoft.VisualStudio.Validation", version: "15.3.32" },
    { id: "stdole", version: "7.0.3303" },
    { id: "StreamJsonRpc", version: "1.4.128" },
    { id: "VSLangProj", version: "7.0.3301" },
    { id: "VSLangProj2", version: "7.0.5001" },
];
