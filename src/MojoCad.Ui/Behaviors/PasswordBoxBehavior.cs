using System.Windows;
using System.Windows.Controls;

namespace MojoCad.Ui.Behaviors
{
    /// <summary>
    /// <see cref="PasswordBox.Password"/> is deliberately not a DependencyProperty (WPF avoids keeping
    /// the secret in the binding/serialization machinery), so we can't bind it directly. This attached
    /// behavior bridges it to a string on the view-model: it pushes VM -&gt; box on assignment and box
    /// -&gt; VM on the PasswordChanged event, with a re-entrancy guard so the two don't fight. We keep
    /// the secret only in the live PasswordBox and the VM field - never in a serialized binding.
    /// </summary>
    public static class PasswordBoxBehavior
    {
        public static readonly DependencyProperty BoundPasswordProperty =
            DependencyProperty.RegisterAttached(
                "BoundPassword",
                typeof(string),
                typeof(PasswordBoxBehavior),
                new FrameworkPropertyMetadata(string.Empty, OnBoundPasswordChanged));

        public static readonly DependencyProperty AttachProperty =
            DependencyProperty.RegisterAttached(
                "Attach",
                typeof(bool),
                typeof(PasswordBoxBehavior),
                new PropertyMetadata(false, OnAttachChanged));

        // Guards against the change-event echo when we set Password from the VM side.
        private static readonly DependencyProperty IsUpdatingProperty =
            DependencyProperty.RegisterAttached(
                "IsUpdating",
                typeof(bool),
                typeof(PasswordBoxBehavior),
                new PropertyMetadata(false));

        public static string GetBoundPassword(DependencyObject d) => (string)d.GetValue(BoundPasswordProperty);
        public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

        public static bool GetAttach(DependencyObject d) => (bool)d.GetValue(AttachProperty);
        public static void SetAttach(DependencyObject d, bool value) => d.SetValue(AttachProperty, value);

        private static bool GetIsUpdating(DependencyObject d) => (bool)d.GetValue(IsUpdatingProperty);
        private static void SetIsUpdating(DependencyObject d, bool value) => d.SetValue(IsUpdatingProperty, value);

        private static void OnAttachChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not PasswordBox box) return;
            if ((bool)e.OldValue) box.PasswordChanged -= OnPasswordChanged;
            if ((bool)e.NewValue) box.PasswordChanged += OnPasswordChanged;
        }

        private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not PasswordBox box) return;
            // Only write through when the change originated on the VM side, otherwise we'd clobber the
            // caret / re-enter the changed handler.
            if (GetIsUpdating(box)) return;

            var newValue = (string?)e.NewValue ?? string.Empty;
            if (box.Password != newValue)
                box.Password = newValue;
        }

        private static void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not PasswordBox box) return;
            SetIsUpdating(box, true);
            SetBoundPassword(box, box.Password);
            SetIsUpdating(box, false);
        }
    }
}
