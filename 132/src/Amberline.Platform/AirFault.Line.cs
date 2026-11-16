using RtmpSharp.IO;

namespace Amberline.Platform;

/// <summary>
/// The root causes as the served AIR line reads them (LolClient 1.52, <see cref="AirLine"/>; batch 794). 1.52's
/// <c>GameException.init</c> registers the UnexpectedServiceException alias to <c>GameException</c> (GameException.as:30, a shell mixin
/// that runs after _LolClient_FlexInit, whose own registration is getClassByAlias-guarded), and its <c>LcdsErrorHandler</c> is 1.45's.
/// </summary>
public sealed partial class AirFault
{
    /// <summary>
    /// The <c>rootCause</c> object: <c>PlatformException</c>'s three fields, typed by the Java class alias, and nothing else. Batch 794:
    /// the AIR 1.52 client registers the UnexpectedServiceException alias to <c>GameException</c> too (GameException.as:30, a shell mixin
    /// that runs after _LolClient_FlexInit), so every root cause, that one included, decodes into a PlatformException class, which declares
    /// only these three; 1.45's message/stackTraceString shape would name fields the class lacks.
    /// </summary>
    public AsObject RootCause() => AirWire.Object(Alias,
        ("errorCode", ErrorCode),
        ("substitutionArguments", SubstitutionArguments.ToArray()),
        ("rootCauseClassname", Alias));

    /// <summary>
    /// A call without a platform session. Batch 794 (AIR 1.52): LcdsErrorHandler's session-timeout branch tests for an
    /// UnexpectedServiceException, which 1.52 never decodes (GameException.as:30), so the 1.45 answer (that exception wrapping
    /// <c>InvalidSessionException</c>) would end in the generic unhandled-error alert, its message and stackTraceString being no
    /// GameException fields. 1.52 logs out as expired on an
    /// <c>InvalidSessionException</c> with an errorCode instead (BaseShell.as:1085, ViewMediatorImpl.as:178: the unhandled
    /// PlatformException path). Reconstruction: retail's code value is not recovered; the client's own session-expired text key is used.
    /// </summary>
    public static AirFault InvalidSession() => new(InvalidSessionClass, "session_sessionExpiredErrorMessage",
        "No authenticated platform session.");

    /// <summary>
    /// A rune combiner refusal (<c>InventoryServiceImpl.useRuneCombiner</c> registers its handler for the UnexpectedServiceException
    /// class only). Batch 794 (AIR 1.52): no alias resolves to that class any more (GameException.as:30), so the handler is reachable
    /// only by a fault with no root cause, and it then throws TypeError #1009 logging <c>exception.toString()</c>
    /// (RuneCombinerController.as:281-293). The refusal goes as an UnexpectedServiceException, read as a GameException whose errorCode
    /// the unhandled-error path shows in the general alert (BaseShell.as:1089, ViewMediatorImpl.as:182); the combiner clears its busy
    /// state on completion. Reconstruction: the code is the combiner's own generic error text.
    /// </summary>
    public static AirFault Unexpected(string message) => new(UnexpectedService, "runeCombiner_combineGenericError", message);
}
