using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MojoCad.Core.Changes;

namespace MojoCad.Ui.Converters
{
    /// <summary>
    /// Resolves a named brush by key. Converters can't carry <see cref="StaticResource"/> references, so
    /// they ask here at conversion time. We prefer a live resource (when an <see cref="Application"/> exists
    /// and has it), but because the chat palette merges the theme into its OWN control resources - not the
    /// app's (AutoCAD owns the Application) - we also carry a hard-coded fallback palette that mirrors
    /// Themes/Dark.xaml verbatim. That guarantees the legend colours are always correct, whether or not an
    /// app-level dictionary is present, and never falls back to an alien grey.
    /// </summary>
    internal static class ThemeBrushes
    {
        // Mirrors Themes/Dark.xaml. Kept in sync by hand; these are the canonical legend/status colours.
        private static readonly Dictionary<string, Brush> Fallback = Build();

        private static Dictionary<string, Brush> Build()
        {
            Brush B(string hex)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
                b.Freeze(); // shareable across threads / bindings
                return b;
            }
            return new Dictionary<string, Brush>(StringComparer.Ordinal)
            {
                ["Brush.Additive"] = B("#FF3FB950"),
                ["Brush.Modify"] = B("#FFD9A23B"),
                ["Brush.Erase"] = B("#FFE5484D"),
                ["Brush.Organizational"] = B("#FF8B95A5"),
                ["Brush.Ok"] = B("#FF3FB950"),
                ["Brush.Warn"] = B("#FFD9A23B"),
                ["Brush.Danger"] = B("#FFE5484D"),
                ["Brush.Accent"] = B("#FF4C8DFF"),
                ["Brush.Text"] = B("#FFE6E8EC"),
                ["Brush.TextMuted"] = B("#FF9AA0AB"),
                ["Brush.TextFaint"] = B("#FF6B7079"),
            };
        }

        public static Brush Lookup(string key)
        {
            if (Application.Current != null && Application.Current.TryFindResource(key) is Brush appBrush)
                return appBrush;
            return Fallback.TryGetValue(key, out var b) ? b : Brushes.Gray;
        }
    }

    /// <summary>Maps an <see cref="OpCategory"/> to its legend brush (green/amber/red/slate).</summary>
    public sealed class CategoryToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = value switch
            {
                OpCategory.Additive => "Brush.Additive",
                OpCategory.Modify => "Brush.Modify",
                OpCategory.Erase => "Brush.Erase",
                OpCategory.Organizational => "Brush.Organizational",
                _ => "Brush.TextMuted"
            };
            return ThemeBrushes.Lookup(key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Maps a <see cref="Severity"/> to its badge brush. Drives the review gating legend.</summary>
    public sealed class SeverityToBrushConverter : IValueConverter
    {
        // Mirrors the Color.Sev* values in Themes/Dark.xaml. Self-contained so the badge colours are
        // correct even when the theme lives in control (not application) resources.
        private static readonly Brush Info = Frozen("#FF6B7079");
        private static readonly Brush Notice = Frozen("#FF4C8DFF");
        private static readonly Brush Warning = Frozen("#FFD9A23B");
        private static readonly Brush Blocking = Frozen("#FFE5484D");

        private static Brush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            b.Freeze();
            return b;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value switch
            {
                Severity.Notice => Notice,
                Severity.Warning => Warning,
                Severity.Blocking => Blocking,
                _ => Info
            };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Standard bool -&gt; Visibility. Pass "Invert" as the parameter to flip the mapping.</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is bool b && b;
            if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
                flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Visible;
    }

    /// <summary>Collapses an element when its bound value is null (or, for strings, empty/whitespace).</summary>
    public sealed class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
            bool invert = parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase);
            bool visible = invert ? empty : !empty;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>True when the bound count is greater than zero (used to hide empty sections).</summary>
    public sealed class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int n = value is int i ? i : 0;
            return n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Maps a connection-state enum to a status dot brush (ok/warn/danger). Used by the footer dot so a
    /// glance tells the user whether their key is good and the agent reachable.
    /// </summary>
    public sealed class ConnectionStateToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = value switch
            {
                ViewModels.ConnectionState.Connected => "Brush.Ok",
                ViewModels.ConnectionState.Working => "Brush.Warn",
                ViewModels.ConnectionState.Error => "Brush.Danger",
                _ => "Brush.TextFaint"
            };
            return ThemeBrushes.Lookup(key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Maps the settings status (key/model test result) to an ok/warn/error brush.</summary>
    public sealed class StatusKindToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = value switch
            {
                ViewModels.SettingsViewModel.StatusKind.Ok => "Brush.Ok",
                ViewModels.SettingsViewModel.StatusKind.Warning => "Brush.Warn",
                ViewModels.SettingsViewModel.StatusKind.Error => "Brush.Danger",
                _ => "Brush.TextMuted"
            };
            return ThemeBrushes.Lookup(key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Negates a bool (for IsEnabled bindings where the source semantics are inverted).</summary>
    public sealed class BoolNegationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => !(value is bool b && b);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => !(value is bool b && b);
    }

}
