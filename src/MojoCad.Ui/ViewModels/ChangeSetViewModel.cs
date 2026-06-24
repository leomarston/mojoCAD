using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MojoCad.Core.Changes;

namespace MojoCad.Ui.ViewModels
{
    /// <summary>
    /// The reviewable body of a change set: the trustworthy header counts (always from
    /// <see cref="ChangeSet.ComputeStats"/>, never a model-supplied number) plus one
    /// <see cref="ProposedOpViewModel"/> per op. It also tracks the "currently ticked" set so the parent
    /// can drive the live preview's visibility as rows toggle.
    /// </summary>
    public sealed partial class ChangeSetViewModel : ObservableObject
    {
        private readonly ChangeSet _changeSet;
        private readonly System.Action<ChangeSetViewModel>? _onSelectionChanged;

        public ChangeSetViewModel(ChangeSet changeSet, System.Action<ChangeSetViewModel>? onSelectionChanged = null)
        {
            _changeSet = changeSet;
            _onSelectionChanged = onSelectionChanged;
            Summary = string.IsNullOrWhiteSpace(changeSet.Summary) ? "Proposed changes" : changeSet.Summary;

            foreach (var op in changeSet.Ops)
                Ops.Add(new ProposedOpViewModel(op, OnRowAcceptanceChanged));

            RefreshStats();
        }

        public string ChangeSetId => _changeSet.Id;
        public string Summary { get; }

        /// <summary>The live op rows. Order matches the change set (later ops may depend on earlier ones).</summary>
        public ObservableCollection<ProposedOpViewModel> Ops { get; } = new();

        // ----- trustworthy header counts (computed, never model-supplied) -----------------------

        [ObservableProperty] private int _totalCount;
        [ObservableProperty] private int _additiveCount;
        [ObservableProperty] private int _modifyCount;
        [ObservableProperty] private int _eraseCount;
        [ObservableProperty] private int _organizationalCount;
        [ObservableProperty] private int _blockingCount;
        [ObservableProperty] private string _layersTouched = string.Empty;
        [ObservableProperty] private int _acceptedCount;

        private void RefreshStats()
        {
            // Counts come from the model's own ops via ComputeStats - the agent can't inflate or hide them.
            var stats = _changeSet.ComputeStats();
            TotalCount = stats.Total;
            AdditiveCount = stats.Additive;
            ModifyCount = stats.Modify;
            EraseCount = stats.Erase;
            OrganizationalCount = stats.Organizational;
            BlockingCount = stats.Blocking;
            LayersTouched = stats.LayersTouched.Count == 0
                ? "—"
                : string.Join(", ", stats.LayersTouched);
            AcceptedCount = Ops.Count(o => o.IsAccepted);
        }

        public bool HasBlocking => BlockingCount > 0;
        partial void OnBlockingCountChanged(int value) => OnPropertyChanged(nameof(HasBlocking));

        /// <summary>Op ids the engineer currently has ticked - the set the preview should show and apply.</summary>
        public IReadOnlyList<string> AcceptedOpIds => Ops.Where(o => o.IsAccepted).Select(o => o.OpId).ToList();

        private void OnRowAcceptanceChanged(ProposedOpViewModel row)
        {
            AcceptedCount = Ops.Count(o => o.IsAccepted);
            _onSelectionChanged?.Invoke(this);
        }

        /// <summary>Tick every non-blocking, non-erase op (the "Accept all" affordance, deletions opt-in).</summary>
        public void AcceptAll()
        {
            foreach (var row in Ops)
            {
                if (row.IsBlocking) continue;
                if (row.Category == OpCategory.Erase) continue;
                row.IsAccepted = true;
            }
        }

        public void RejectAll()
        {
            foreach (var row in Ops)
                row.IsAccepted = false;
        }

        /// <summary>Write the current ticks into the underlying ops, ready for an atomic apply.</summary>
        public void CommitAcceptanceToModel()
        {
            foreach (var row in Ops)
                row.CommitAcceptanceToModel();
        }

        /// <summary>Pull each op's post-apply state back onto its row (applied/failed styling).</summary>
        public void ReflectModelStates()
        {
            foreach (var row in Ops)
                row.ReflectModelState();
        }

        /// <summary>A short summary used in the undo-group label, e.g. "+3 ~1 layers A-WALL".</summary>
        public string BuildShortSummary()
        {
            if (!string.IsNullOrWhiteSpace(_changeSet.Summary))
                return _changeSet.Summary.Length > 60
                    ? _changeSet.Summary.Substring(0, 60)
                    : _changeSet.Summary;

            var parts = new List<string>();
            if (AdditiveCount > 0) parts.Add($"+{AdditiveCount}");
            if (ModifyCount > 0) parts.Add($"~{ModifyCount}");
            if (EraseCount > 0) parts.Add($"-{EraseCount}");
            return parts.Count > 0 ? string.Join(" ", parts) : "changes";
        }
    }
}
