using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MojoCad.Ui.ViewModels
{
    /// <summary>
    /// Base type for everything that appears in the transcript. Subclasses are matched to their visual
    /// by implicit <c>DataType</c> DataTemplates in ChatView.xaml (one template per concrete type), so
    /// the chat list never needs to know about the specific kinds - it just renders the collection.
    /// </summary>
    public abstract partial class MessageViewModel : ObservableObject
    {
        /// <summary>Wall-clock time the message entered the transcript (used for subtle timestamps).</summary>
        public DateTime Timestamp { get; } = DateTime.Now;
    }
}
