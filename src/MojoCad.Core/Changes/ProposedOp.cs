using System;
using System.Collections.Generic;
using System.Linq;
using MojoCad.Core.Geometry;

namespace MojoCad.Core.Changes
{
    /// <summary>
    /// A single, fully-described, AutoCAD-free operation that the agent has proposed. A
    /// <see cref="ProposedOp"/> carries everything the Acad adapter needs to (a) build a transient
    /// <i>preview</i> and (b) <i>commit</i> the change inside an undo group. It is the only currency
    /// the agent can use to affect the drawing: agent write-tools append <c>ProposedOp</c>s to the
    /// pending <see cref="ChangeSet"/>; they never touch the AutoCAD <c>Database</c> directly.
    /// </summary>
    public abstract class ProposedOp
    {
        /// <summary>Stable id for this op within its change set (e.g. "op-3"). Used by the UI and audit log.</summary>
        public string OpId { get; set; } = string.Empty;

        /// <summary>The concrete operation kind.</summary>
        public abstract OpKind Kind { get; }

        /// <summary>The review/preview category (drives the colour legend).</summary>
        public abstract OpCategory Category { get; }

        /// <summary>Target layer for created geometry. Null = use the current layer.</summary>
        public string? Layer { get; set; }

        /// <summary>
        /// Discouraged per-entity property overrides (colour/linetype/lineweight). BYLAYER is the
        /// default and strongly preferred; populated only when the agent explicitly overrides.
        /// </summary>
        public PropertyOverrides? Props { get; set; }

        /// <summary>One-line, human-readable description shown on the review row (authored by the agent).</summary>
        public string PlainLanguage { get; set; } = string.Empty;

        /// <summary>Severity for review gating. Defaults to <see cref="Severity.Info"/>.</summary>
        public Severity Severity { get; set; } = Severity.Info;

        /// <summary>Current lifecycle state. Starts <see cref="ChangeState.Proposed"/>.</summary>
        public ChangeState State { get; set; } = ChangeState.Proposed;

        /// <summary>
        /// Handles of existing entities this op reads or mutates (move/erase/etc.). Empty for pure creates.
        /// </summary>
        public List<string> TargetHandles { get; } = new List<string>();

        /// <summary>
        /// Handles of entities produced by this op, filled in by the Acad adapter after a successful apply.
        /// (Provisional ids may be assigned at stage time so the agent can reference them in later ops.)
        /// </summary>
        public List<string> ResultHandles { get; } = new List<string>();

        /// <summary>If apply failed, the reason (surfaced on the row). Null otherwise.</summary>
        public string? FailureReason { get; set; }

        public bool IsAccepted => State == ChangeState.Accepted;
    }

    /// <summary>Per-entity property overrides. All optional; null means "leave BYLAYER".</summary>
    public sealed class PropertyOverrides
    {
        /// <summary>AutoCAD Color Index (1-255), or null.</summary>
        public int? ColorIndex { get; set; }

        /// <summary>True-colour as 0xRRGGBB, or null.</summary>
        public int? TrueColor { get; set; }

        public string? Linetype { get; set; }

        /// <summary>Lineweight in hundredths of a millimetre (e.g. 25 = 0.25mm), or null for BYLAYER.</summary>
        public int? LineweightHundredthsMm { get; set; }
    }

    // ----- Additive primitives ------------------------------------------------------------------

    public sealed class CreatePolylineOp : ProposedOp
    {
        public override OpKind Kind => OpKind.CreatePolyline;
        public override OpCategory Category => OpCategory.Additive;

        public List<Pt> Points { get; set; } = new List<Pt>();
        public bool Closed { get; set; }

        /// <summary>Optional per-vertex bulge factors (arc segments). Length must equal Points.Count when present.</summary>
        public List<double>? Bulges { get; set; }

        /// <summary>Optional constant width applied to the whole polyline.</summary>
        public double? GlobalWidth { get; set; }
    }

    public sealed class CreateCircleOp : ProposedOp
    {
        public override OpKind Kind => OpKind.CreateCircle;
        public override OpCategory Category => OpCategory.Additive;
        public Pt Center { get; set; }
        public double Radius { get; set; }
    }

    public sealed class CreateArcOp : ProposedOp
    {
        public override OpKind Kind => OpKind.CreateArc;
        public override OpCategory Category => OpCategory.Additive;
        public Pt Center { get; set; }
        public double Radius { get; set; }
        public double StartAngleDeg { get; set; }
        public double EndAngleDeg { get; set; }
    }

    public sealed class CreateRectangleOp : ProposedOp
    {
        public override OpKind Kind => OpKind.CreateRectangle;
        public override OpCategory Category => OpCategory.Additive;
        public Pt Corner1 { get; set; }
        public Pt Corner2 { get; set; }
        public double RotationDeg { get; set; }
        public double? FilletRadius { get; set; }
    }

    public sealed class CreateEllipseOp : ProposedOp
    {
        public override OpKind Kind => OpKind.CreateEllipse;
        public override OpCategory Category => OpCategory.Additive;
        public Pt Center { get; set; }
        public Vec MajorAxis { get; set; }
        public double Ratio { get; set; }
    }

    public enum TextJustify { Left, Center, Right, Middle }

    public sealed class CreateTextOp : ProposedOp
    {
        public override OpKind Kind => OpKind.CreateText;
        public override OpCategory Category => OpCategory.Additive;
        public string Contents { get; set; } = string.Empty;
        public Pt Position { get; set; }
        public double Height { get; set; }
        public double RotationDeg { get; set; }
        public string? Style { get; set; }
        public TextJustify Justify { get; set; } = TextJustify.Left;
        public bool IsMText { get; set; }
        public double? Width { get; set; }
    }

    public enum DimensionKind { Linear, Aligned, Angular, Radial, Diameter }

    public sealed class CreateDimensionOp : ProposedOp
    {
        public override OpKind Kind => OpKind.CreateDimension;
        public override OpCategory Category => OpCategory.Additive;
        public DimensionKind DimKind { get; set; }
        public Pt? P1 { get; set; }
        public Pt? P2 { get; set; }
        public Pt? LineLocation { get; set; }
        public string? DimStyle { get; set; }
        public string? TextOverride { get; set; }
    }

    public sealed class CreateHatchOp : ProposedOp
    {
        public override OpKind Kind => OpKind.CreateHatch;
        public override OpCategory Category => OpCategory.Additive;
        public string Pattern { get; set; } = "SOLID";
        public List<string>? BoundaryIds { get; set; }
        public List<Pt>? BoundaryPoints { get; set; }
        public double Scale { get; set; } = 1.0;
        public double AngleDeg { get; set; }
    }

    public sealed class InsertBlockOp : ProposedOp
    {
        public override OpKind Kind => OpKind.InsertBlock;
        public override OpCategory Category => OpCategory.Additive;
        public string BlockName { get; set; } = string.Empty;
        public Pt Position { get; set; }
        public double Scale { get; set; } = 1.0;
        public double RotationDeg { get; set; }
        public Dictionary<string, string>? Attributes { get; set; }
    }

    // ----- Organisational -----------------------------------------------------------------------

    public sealed class CreateLayerOp : ProposedOp
    {
        public override OpKind Kind => OpKind.CreateLayer;
        public override OpCategory Category => OpCategory.Organizational;
        public string Name { get; set; } = string.Empty;
        public int? ColorIndex { get; set; }
        public string? Linetype { get; set; }
        public int? LineweightHundredthsMm { get; set; }
        public string? Description { get; set; }
    }

    public sealed class SetCurrentLayerOp : ProposedOp
    {
        public override OpKind Kind => OpKind.SetCurrentLayer;
        public override OpCategory Category => OpCategory.Organizational;
        public string Name { get; set; } = string.Empty;
    }

    // ----- Editing existing entities (reference by handle) ---------------------------------------

    public sealed class MoveOp : ProposedOp
    {
        public override OpKind Kind => OpKind.Move;
        public override OpCategory Category => OpCategory.Modify;
        public Pt From { get; set; }
        public Pt To { get; set; }
    }

    public sealed class CopyOp : ProposedOp
    {
        public override OpKind Kind => OpKind.Copy;
        public override OpCategory Category => OpCategory.Additive; // produces new entities
        public Pt From { get; set; }
        public Pt To { get; set; }
        public int Count { get; set; } = 1;
    }

    public sealed class RotateOp : ProposedOp
    {
        public override OpKind Kind => OpKind.Rotate;
        public override OpCategory Category => OpCategory.Modify;
        public Pt Base { get; set; }
        public double AngleDeg { get; set; }
    }

    public sealed class ScaleOp : ProposedOp
    {
        public override OpKind Kind => OpKind.Scale;
        public override OpCategory Category => OpCategory.Modify;
        public Pt Base { get; set; }
        public double Factor { get; set; } = 1.0;
    }

    public sealed class MirrorOp : ProposedOp
    {
        public override OpKind Kind => OpKind.Mirror;
        // Additive when keeping the original (creates a mirrored copy), Modify otherwise.
        public override OpCategory Category => KeepOriginal ? OpCategory.Additive : OpCategory.Modify;
        public Pt AxisP1 { get; set; }
        public Pt AxisP2 { get; set; }
        public bool KeepOriginal { get; set; } = true;
    }

    public sealed class OffsetOp : ProposedOp
    {
        public override OpKind Kind => OpKind.Offset;
        public override OpCategory Category => OpCategory.Additive; // offset produces a new curve
        public double Distance { get; set; }

        /// <summary>A point on the side to offset toward (preferred), or null to use <see cref="Direction"/>.</summary>
        public Pt? Side { get; set; }

        /// <summary>A direction vector when <see cref="Side"/> is not given.</summary>
        public Vec? Direction { get; set; }
    }

    public enum ArrayKind { Rectangular, Polar }

    public sealed class ArrayOp : ProposedOp
    {
        public override OpKind Kind => OpKind.Array;
        public override OpCategory Category => OpCategory.Additive;
        public ArrayKind ArrayKind { get; set; }
        public int Rows { get; set; } = 1;
        public int Cols { get; set; } = 1;
        public double RowSpacing { get; set; }
        public double ColSpacing { get; set; }
        public Pt? Center { get; set; }
        public int Count { get; set; }
        public double AngleFillDeg { get; set; } = 360.0;
    }

    public sealed class EraseOp : ProposedOp
    {
        public override OpKind Kind => OpKind.Erase;
        public override OpCategory Category => OpCategory.Erase;
    }

    public sealed class PropertyChangeOp : ProposedOp
    {
        public override OpKind Kind => OpKind.PropertyChange;
        public override OpCategory Category => OpCategory.Modify;

        /// <summary>New layer to move the entities onto, or null to leave unchanged.</summary>
        public string? NewLayer { get; set; }

        /// <summary>New property overrides to apply, or null to leave unchanged.</summary>
        public PropertyOverrides? NewProps { get; set; }
    }
}
