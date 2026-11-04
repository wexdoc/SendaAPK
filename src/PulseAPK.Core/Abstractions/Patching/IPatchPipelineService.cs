// Modified for SendaAPK (2026); original work remains credited in NOTICE.
using PulseAPK.Core.Models;

namespace PulseAPK.Core.Abstractions.Patching;

public interface IPatchPipelineService
{
    Task<PatchResult> RunAsync(PatchRequest request, CancellationToken cancellationToken = default);
    Task<PatchResult> RunAsync(PatchRequest request, CancellationToken cancellationToken, Action<string> progress)
        => RunAsync(request, cancellationToken);
}
