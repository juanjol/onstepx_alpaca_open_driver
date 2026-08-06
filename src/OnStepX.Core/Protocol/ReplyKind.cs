namespace OnStepX.Core.Protocol;

/// <summary>
/// Shape of the response produced by an OnStepX command.
/// </summary>
/// <remarks>
/// It must be declared per command because the firmware gives no hint in
/// the response itself. In <c>ProcessCmds.cpp</c> the processor decides
/// like this:
/// <list type="bullet">
///   <item>
///     If <c>numericReply</c> is still active, the response is <c>"1"</c>
///     or <c>"0"</c> and <c>suppressFrame</c> is also forced, meaning
///     <b>a single character with no terminator</b> arrives.
///   </item>
///   <item>
///     If the command wrote text into the buffer, the response ends in
///     <c>#</c> unless the handler asks to suppress the frame.
///   </item>
///   <item>
///     If it wrote nothing and there is no checksum, <b>absolutely nothing
///     is sent</b>.
///   </item>
/// </list>
/// The useful exception: with checksum enabled the condition is
/// <c>strlen(reply) &gt; 0 || buffer.checksum</c> and <c>suppressFrame =
/// false</c> is forced, so <b>every</b> command responds and <b>always</b>
/// ends in <c>#</c>. That is why error correction mode simplifies the
/// channel instead of complicating it.
/// </remarks>
public enum ReplyKind
{
    /// <summary>
    /// The command answers nothing in normal mode. Examples: <c>:FQ#</c>,
    /// <c>:Mn#</c>, <c>:TQ#</c>, <c>:F+#</c>.
    /// </summary>
    None,

    /// <summary>
    /// A single character <c>1</c> or <c>0</c>, with no terminator in
    /// normal mode. Examples: <c>:Te#</c>, <c>:hP#</c>, <c>:SrHH:MM:SS#</c>.
    /// </summary>
    Boolean,

    /// <summary>
    /// Payload ending in <c>#</c>. Examples: <c>:GVP#</c>, <c>:GR#</c>,
    /// <c>:GU#</c>, <c>:Fg#</c>.
    /// </summary>
    Terminated,

    /// <summary>
    /// A single digit with no terminator, from <c>0</c> to <c>9</c>. This
    /// is the case for gotos: <c>:MS#</c>, <c>:MA#</c>, <c>:MN#</c>,
    /// <c>:MP#</c> and <c>:MD#</c>.
    /// </summary>
    SingleDigit,
}
