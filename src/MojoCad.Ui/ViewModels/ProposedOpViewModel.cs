using System;
using System.Globalization;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MojoCad.Core.Changes;
using MojoCad.Core.Geometry;

namespace MojoCad.Ui.ViewModels
{
    /// <summary>
    /// One reviewable row. Wraps a <see cref="ProposedOp"/> and exposes display-ready properties plus the
    /// two-way <see cref="IsAccepted"/> tick. Default acceptance follows the house rules: additive and
    /// organisational ops start ticked; modify/erase and anything Blocking start un-ticked, and Blocking
    /// rows can't be ticked at all until the engineer resolves the underlying issue.
    ///
    /// Ticking a row notifies the parent <see cref="ChangeSetViewModel"/> so it can re-scope the live
    /// preview and refresh aggregate counts - the model never decides what gets applied, the engineer does.
    /// </summary>
    public sealed partial class ProposedOpViewModel : ObservableObject
    {
        private readonly ProposedOp _op;
        private readonly Action<ProposedOpViewModel>? _onAcceptanceChanged;

        public ProposedOpViewModel(ProposedOp op, Action<ProposedOpViewModel>? onAcceptanceChanged = null)
        {
            _op = op;
            _onAcceptanceChanged = onAcceptanceChanged;

            // Blocking ops can never be applied until resolved, so they're disabled AND default off.
            IsBlocking = op.Severity == Severity.Blocking;

            bool defaultOn = !IsBlocking &&
                             op.Category is OpCategory.Additive or OpCategory.Organizational;
            _isAccepted = defaultOn;

            Description = BuildDescription(op);
        }

        public string OpId => _op.OpId;
        public OpKind Kind => _op.Kind;
        public OpCategory Category => _op.Category;
        public Severity Severity => _op.Severity;
        public string Layer => string.IsNullOrWhiteSpace(_op.Layer) ? "(current)" : _op.Layer!;
        public string Description { get; }

        /// <summary>Short, all-caps category label for the colour badge.</summary>
        public string CategoryLabel => Category switch
        {
            OpCategory.Additive => "ADD",
            OpCategory.Modify => "MODIFY",
            OpCategory.Erase => "ERASE",
            OpCategory.Organizational => "ORG",
            _ => Category.ToString().ToUpperInvariant()
        };

        public string SeverityLabel => Severity.ToString().ToUpperInvariant();

        /// <summary>Severity badge only shows for anything above the baseline Info noise.</summary>
        public bool ShowSeverity => Severity != Severity.Info;

        public bool IsBlocking { get; }

        /// <summary>The row's checkbox is enabled only when the op isn't Blocking.</summary>
        public bool CanAccept => !IsBlocking;

        // ----- lint -----------------------------------------------------------------------------

        [ObservableProperty]
        private string? _lintMessage;

        [ObservableProperty]
        private string? _lintHint;

        public bool HasLint => !string.IsNullOrWhiteSpace(LintMessage);

        partial void OnLintMessageChanged(string? value) => OnPropertyChanged(nameof(HasLint));

        public void AttachLint(string message, string? hint)
        {
            LintMessage = message;
            LintHint = hint;
        }

        // ----- acceptance -----------------------------------------------------------------------

        [ObservableProperty]
        private bool _isAccepted;

        partial void OnIsAcceptedChanged(bool value)
        {
            // Blocking rows are disabled in the view, but guard here too so nothing can sneak one on.
            if (IsBlocking && value)
            {
                _isAccepted = false;
                OnPropertyChanged(nameof(IsAccepted));
                return;
            }
            _onAcceptanceChanged?.Invoke(this);
        }

        // ----- apply outcome --------------------------------------------------------------------

        /// <summary>Set after an apply so the row can render an applied/failed treatment.</summary>
        [ObservableProperty]
        private ChangeState _displayState = ChangeState.Proposed;

        [ObservableProperty]
        private string? _failureReason;

        public bool IsApplied => DisplayState == ChangeState.Applied;
        public bool IsFailed => DisplayState == ChangeState.Failed;

        partial void OnDisplayStateChanged(ChangeState value)
        {
            OnPropertyChanged(nameof(IsApplied));
            OnPropertyChanged(nameof(IsFailed));
        }

        /// <summary>Push the engineer's tick into the underlying op's lifecycle state before an apply.</summary>
        public void CommitAcceptanceToModel()
        {
            _op.State = IsAccepted ? ChangeState.Accepted : ChangeState.Rejected;
        }

        /// <summary>Reflect the apply outcome from the model back onto the row.</summary>
        public void ReflectModelState()
        {
            DisplayState = _op.State;
            FailureReason = _op.FailureReason;
        }

        // ----- description synthesis ------------------------------------------------------------

        /// <summary>
        /// Prefer the agent-authored plain-language line. When it's missing we synthesize a faithful,
        /// non-editorialised description from the op's own data so a row is never blank or misleading.
        /// </summary>
        private static string BuildDescription(ProposedOp op)
        {
            if (!string.IsNullOrWhiteSpace(op.PlainLanguage))
                return op.PlainLanguage;

            string layer = string.IsNullOrWhiteSpace(op.Layer) ? "" : $" on {op.Layer}";
            switch (op)
            {
                case CreatePolylineOp p:
                    return $"Draw {(p.Closed ? "closed " : "")}polyline ({p.Points.Count} vertices){layer}";
                case CreateCircleOp c:
                    return $"Draw circle r={Fmt(c.Radius)}{layer}";
                case CreateArcOp a:
                    return $"Draw arc r={Fmt(a.Radius)} ({Fmt(a.StartAngleDeg)}°→{Fmt(a.EndAngleDeg)}°){layer}";
                case CreateRectangleOp r:
                    return $"Draw rectangle{layer}";
                case CreateEllipseOp:
                    return $"Draw ellipse{layer}";
                case CreateTextOp t:
                    var snippet = t.Contents.Length > 32 ? t.Contents.Substring(0, 32) + "…" : t.Contents;
                    return $"Place {(t.IsMText ? "mtext" : "text")} “{snippet}”{layer}";
                case CreateDimensionOp d:
                    return $"Add {d.DimKind.ToString().ToLowerInvariant()} dimension{layer}";
                case CreateHatchOp h:
                    return $"Hatch ({h.Pattern}){layer}";
                case InsertBlockOp ib:
                    return $"Insert block “{ib.BlockName}”{layer}";
                case CreateLayerOp cl:
                    return $"Create layer “{cl.Name}”";
                case SetCurrentLayerOp sl:
                    return $"Set current layer to “{sl.Name}”";
                case MoveOp m:
                    return $"Move {Targets(op)} {Fmt(m.From.DistanceTo(m.To))} units";
                case CopyOp cp:
                    return $"Copy {Targets(op)} ({cp.Count}×){layer}";
                case RotateOp ro:
                    return $"Rotate {Targets(op)} {Fmt(ro.AngleDeg)}°";
                case ScaleOp sc:
                    return $"Scale {Targets(op)} by {Fmt(sc.Factor)}";
                case MirrorOp mi:
                    return $"Mirror {Targets(op)}{(mi.KeepOriginal ? " (keep original)" : "")}";
                case OffsetOp of:
                    return $"Offset {Targets(op)} by {Fmt(of.Distance)}{layer}";
                case ArrayOp ar:
                    return ar.ArrayKind == ArrayKind.Polar
                        ? $"Polar array {Targets(op)} ({ar.Count})"
                        : $"Rectangular array {Targets(op)} ({ar.Rows}×{ar.Cols})";
                case EraseOp:
                    return $"Erase {Targets(op)}";
                case PropertyChangeOp pc:
                    var bits = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(pc.NewLayer)) bits.Append($"move to {pc.NewLayer}");
                    if (pc.NewProps != null)
                    {
                        if (bits.Length > 0) bits.Append(", ");
                        bits.Append("change properties");
                    }
                    var what = bits.Length > 0 ? bits.ToString() : "change properties";
                    return $"{Cap(what)} of {Targets(op)}";
                default:
                    return $"{op.Kind}{layer}";
            }
        }

        private static string Targets(ProposedOp op)
        {
            int n = op.TargetHandles.Count;
            return n switch
            {
                0 => "selection",
                1 => "1 entity",
                _ => $"{n} entities"
            };
        }

        private static string Fmt(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Cap(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
