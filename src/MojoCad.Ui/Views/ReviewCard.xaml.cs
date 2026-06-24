using System.Windows;
using System.Windows.Controls;
using MojoCad.Ui.ViewModels;

namespace MojoCad.Ui.Views
{
    /// <summary>
    /// The change-set review card. Code-behind only handles the row-hover preview flash (a pure view
    /// gesture that doesn't belong on a command); accept/reject/undo and the tick logic all live on the
    /// <see cref="ReviewCardViewModel"/>.
    /// </summary>
    public partial class ReviewCard : UserControl
    {
        public ReviewCard()
        {
            InitializeComponent();
        }

        private void Row_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Hovering a row briefly emphasises its preview in the drawing (no zoom).
            if (DataContext is ReviewCardViewModel card &&
                sender is FrameworkElement fe &&
                fe.DataContext is ProposedOpViewModel row)
            {
                if (card.FlashRowCommand.CanExecute(row.OpId))
                    card.FlashRowCommand.Execute(row.OpId);
            }
        }
    }
}
