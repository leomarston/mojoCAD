using System.Collections.Generic;
using System.Threading.Tasks;
using MojoCad.Core.Changes;

namespace MojoCad.Core.Ports
{
    /// <summary>
    /// Renders a change set as a side-effect-free <i>preview</i> using AutoCAD transient graphics and
    /// entity highlighting - <b>no database writes</b>. This is what makes Reject free: the drawing is
    /// never dirtied, no reactors fire, autosave is untouched. Implemented by the Acad adapter.
    /// </summary>
    public interface IChangePreviewer
    {
        /// <summary>
        /// Draw the change set. Additive ops show as green transient ghosts, modify ops as amber ghosts
        /// of the new state, erase ops as a red highlight/halo on the doomed entities.
        /// </summary>
        Task ShowAsync(ChangeSet changeSet);

        /// <summary>Restrict the preview to a subset of op ids (as the engineer ticks/unticks rows).</summary>
        Task UpdateVisibilityAsync(IReadOnlyCollection<string> visibleOpIds);

        /// <summary>Briefly emphasise one op's preview (row hover) and/or zoom to it.</summary>
        Task FlashAsync(string opId, bool zoom);

        /// <summary>Tear down all transients and remove all highlights. Cheap and side-effect-free.</summary>
        Task ClearAsync();
    }

    /// <summary>
    /// Commits the accepted subset of a change set to the drawing as one atomic, named undo group.
    /// Implemented by the Acad adapter via <c>ExecuteInCommandContextAsync</c> → <c>LockDocument</c> →
    /// <c>StartUndoMarker</c>/transaction(s)/<c>EndUndoMarker</c>. All-or-nothing.
    /// </summary>
    public interface IChangeApplier
    {
        /// <summary>
        /// Apply the accepted ops of <paramref name="changeSet"/>. The whole apply collapses to a single
        /// Ctrl+Z step labelled <paramref name="undoLabel"/>. If any op throws, the entire apply rolls
        /// back and the result reports failure - we never leave a half-applied life-safety change.
        /// </summary>
        Task<ApplyResult> ApplyAsync(ChangeSet changeSet, string undoLabel);

        /// <summary>Undo the most recent applied change set (the inline "Undo" affordance on the applied banner).</summary>
        Task UndoLastAsync();
    }
}
