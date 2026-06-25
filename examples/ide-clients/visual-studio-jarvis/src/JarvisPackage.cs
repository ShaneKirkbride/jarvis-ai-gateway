using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace Jarvis.VisualStudio.Example;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("Jarvis Gateway Example", "Example Visual Studio client for Jarvis AI Gateway", "0.1.0")]
[ProvideOptionPage(typeof(JarvisOptions), "Jarvis Gateway", "General", 0, 0, true)]
[Guid(PackageGuidString)]
public sealed class JarvisPackage : AsyncPackage
{
    public const string PackageGuidString = "8f6efbb8-793d-4d7e-9a15-f0d9f7dd348f";

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        // Command and tool-window registration are intentionally omitted from this source-only sample.
        // Keep all network work off the UI thread in a production VSIX command handler.
    }
}
