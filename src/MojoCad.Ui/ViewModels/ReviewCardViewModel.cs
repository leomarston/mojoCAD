using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MojoCad.Core.Changes;

namespace MojoCad.Ui.ViewModels
{
    /// <summary>The host operations a review card needs. The <see cref="ChatViewModel"/> supplies these so
    /// the card stays free of any direct service reference (and so all service calls are funnelled through
    /// one place that already lives on the UI thread).</summary>
    public interface IReviewHost
    {
        /// <summary>Apply the accepted subset; returns the result so the card can render success/failure.</summary>
        Task<ApplyResult> ApplyAsync(ChangeSet changeSet, string undoLabel);

        /// <summary>Record an applied change set to the audit log.</summary>
        Task RecordAuditAsync(ChangeSet changeSet, ApplyResult result);

        /// <summary>Tear down the live preview transients.</summary>
        Task ClearPreviewAsync();

        /// <summary>Re-scope the preview to the currently-ticked op ids.</summary>
        Task UpdatePreviewVisibilityAsync(System.Collections.Generic.IReadOnlyList<string> visibleOpIds);

        /// <summary>Briefly emphasise (and optionally zoom to) one op's preview.</summary>
        Task FlashAsync(string opId, bool zoom);

        /// <summary>Undo the most recently applied change set.</summary>
        Task UndoLastAsync();

        /// <summary>Surface a transient error line (e.g. an apply failure) in the transcript footer.</summary>
        void ReportError(string message);
    }

    /// <summary>
    /// The review card message: header counts, the op rows, and the accept/reject/undo workflow. It is the
    /// only place changes ever reach the drawing, and only on explicit user action. Hover/zoom affordances
    /// drive the live preview; ticking rows re-scopes it; Accept applies the ticked subset atomically.
    /// </summary>
    public sealed partial class ReviewCardViewModel : MessageViewModel
    {
        private readonly ChangeSet _changeSet;
        private readonly IReviewHost _host;

        public ReviewCardViewModel(ChangeSet changeSet, IReviewHost host)
        {
            _changeSet = changeSet;
            _host = host;
            ChangeSet = new ChangeSetViewModel(changeSet, OnSelectionChanged);
        }

        public ChangeSetViewModel ChangeSet { get; }

        /// <summary>Card lifecycle: Pending (review), Applied (committed), Rejected, or Failed.</summary>
        [ObservableProperty]
        private ReviewCardState _state = ReviewCardState.Pending;

        [ObservableProperty]
        private string? _statusMessage;

        [ObservableProperty]
        private bool _isApplying;

        public bool IsPending => State == ReviewCardState.Pending;
        public bool IsApplied => State == ReviewCardState.Applied;
        public bool IsRejected => State == ReviewCardState.Rejected;

        partial void OnStateChanged(ReviewCardState value)
        {
            OnPropertyChanged(nameof(IsPending));
            OnPropertyChanged(nameof(IsApplied));
            OnPropertyChanged(nameof(IsRejected));
            AcceptCommand.NotifyCanExecuteChanged();
            RejectCommand.NotifyCanExecuteChanged();
            UndoCommand.NotifyCanExecuteChanged();
        }

        partial void OnIsApplyingChanged(bool value)
        {
            AcceptCommand.NotifyCanExecuteChanged();
            RejectCommand.NotifyCanExecuteChanged();
        }

        private bool CanAct() => IsPending && !IsApplying;

        // ----- bulk tick affordances ------------------------------------------------------------

        [RelayCommand]
        private void AcceptAll() => ChangeSet.AcceptAll();

        [RelayCommand]
        private void RejectAllRows() => ChangeSet.RejectAll();

        // ----- preview affordances --------------------------------------------------------------

        private async void OnSelectionChanged(ChangeSetViewModel _)
        {
            // Re-scope the live preview to whatever is currently ticked. Fire-and-forget is fine: it's a
            // cheap, idempotent transient redraw and we always pass the full current set.
            try { await _host.UpdatePreviewVisibilityAsync(ChangeSet.AcceptedOpIds); }
            catch { /* preview is best-effort; never let it disrupt review */ }
        }

        [RelayCommand]
        private async Task FlashRow(string? opId)
        {
            if (string.IsNullOrEmpty(opId)) return;
            try { await _host.FlashAsync(opId!, zoom: false); } catch { }
        }

        [RelayCommand]
        private async Task ZoomRow(string? opId)
        {
            if (string.IsNullOrEmpty(opId)) return;
            try { await _host.FlashAsync(opId!, zoom: true); } catch { }
        }

        // ----- accept / reject / undo -----------------------------------------------------------

        [RelayCommand(CanExecute = nameof(CanAct))]
        private async Task Accept()
        {
            // Push the engineer's ticks into the model, then apply the accepted subset atomically.
            ChangeSet.CommitAcceptanceToModel();

            if (ChangeSet.AcceptedCount == 0)
            {
                StatusMessage = "Nothing is ticked - tick the operations you want before accepting.";
                return;
            }

            IsApplying = true;
            StatusMessage = "Applying…";
            try
            {
                string label = "mojoCAD: " + ChangeSet.BuildShortSummary();
                var result = await _host.ApplyAsync(_changeSet, label);

                // Reflect per-op outcomes regardless of overall success so failed rows are visible.
                ChangeSet.ReflectModelStates();

                if (result.Success)
                {
                    await _host.RecordAuditAsync(_changeSet, result);
                    await _host.ClearPreviewAsync();
                    State = ReviewCardState.Applied;
                    StatusMessage = $"Applied {result.AppliedOpIds.Count} operation(s) as a single undo step.";
                }
                else
                {
                    // Apply is all-or-nothing: on failure nothing was committed, so keep the card live for retry.
                    State = ReviewCardState.Pending;
                    StatusMessage = string.IsNullOrWhiteSpace(result.Error)
                        ? "Apply failed and was rolled back. Nothing was changed."
                        : $"Apply failed and was rolled back: {result.Error}";
                    _host.ReportError(StatusMessage);
                }
            }
            catch (Exception ex)
            {
                State = ReviewCardState.Pending;
                StatusMessage = $"Apply failed: {ex.Message}";
                _host.ReportError(StatusMessage);
            }
            finally
            {
                IsApplying = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanAct))]
        private async Task Reject()
        {
            try { await _host.ClearPreviewAsync(); } catch { }
            _changeSet.RejectAll();
            ChangeSet.ReflectModelStates();
            State = ReviewCardState.Rejected;
            StatusMessage = "Rejected. The drawing was never touched.";
        }

        private bool CanUndo() => IsApplied && !IsApplying;

        [RelayCommand(CanExecute = nameof(CanUndo))]
        private async Task Undo()
        {
            IsApplying = true;
            try
            {
                await _host.UndoLastAsync();
                State = ReviewCardState.Pending;
                StatusMessage = "Undone. You can review and re-apply.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Undo failed: {ex.Message}";
                _host.ReportError(StatusMessage);
            }
            finally
            {
                IsApplying = false;
                UndoCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public enum ReviewCardState
    {
        Pending,
        Applied,
        Rejected,
        Failed
    }
}
