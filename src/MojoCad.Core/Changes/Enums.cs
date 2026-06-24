namespace MojoCad.Core.Changes
{
    /// <summary>The concrete kind of a <see cref="ProposedOp"/>. Mirrors the WRITE tool surface.</summary>
    public enum OpKind
    {
        CreatePolyline,
        CreateCircle,
        CreateArc,
        CreateRectangle,
        CreateEllipse,
        CreateText,
        CreateDimension,
        CreateHatch,
        InsertBlock,
        CreateLayer,
        SetCurrentLayer,
        Move,
        Copy,
        Rotate,
        Scale,
        Mirror,
        Offset,
        Array,
        Erase,
        PropertyChange
    }

    /// <summary>
    /// The visual review category. Drives the canonical colour legend shown on the review card:
    /// Green = new geometry, Amber = modified, Red = deleted, Slate = organisational (no geometry).
    /// </summary>
    public enum OpCategory
    {
        Additive,
        Modify,
        Erase,
        Organizational
    }

    /// <summary>Lifecycle of an individual proposed operation (and, in aggregate, of a change set).</summary>
    public enum ChangeState
    {
        /// <summary>Staged by the agent, awaiting the engineer's decision.</summary>
        Proposed,

        /// <summary>The engineer ticked this op to be applied.</summary>
        Accepted,

        /// <summary>The engineer un-ticked / rejected this op.</summary>
        Rejected,

        /// <summary>Committed to the drawing database.</summary>
        Applied,

        /// <summary>Apply was attempted but threw; nothing for this op was committed.</summary>
        Failed
    }

    /// <summary>
    /// How seriously to treat an operation. <see cref="Blocking"/> ops cannot be accepted until the
    /// engineer resolves the underlying issue (used for life-safety lint failures).
    /// </summary>
    public enum Severity
    {
        Info,
        Notice,
        Warning,
        Blocking
    }
}
