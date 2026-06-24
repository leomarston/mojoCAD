using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MojoCad.Ui.Views
{
    /// <summary>
    /// A tiny, dependency-free markdown renderer for assistant prose. We deliberately avoid pulling in a
    /// full markdown library: the agent emits simple formatting (paragraphs, bullet lists, **bold**,
    /// *italic*, `inline code`, fenced ```code``` blocks, and #/## headings), and a hand-rolled, total
    /// (never-throwing) parser keeps the UI's dependency surface minimal and the output predictable.
    /// Anything it doesn't recognise is rendered verbatim as text - so it degrades to plain wrapping text
    /// rather than ever losing content.
    /// </summary>
    public sealed class MarkdownPresenter : Control
    {
        static MarkdownPresenter()
        {
            // No XAML template - we own the visual tree via a single child panel.
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(MarkdownPresenter),
                new FrameworkPropertyMetadata(typeof(MarkdownPresenter)));
        }

        private readonly StackPanel _root = new StackPanel();

        public MarkdownPresenter()
        {
            AddVisualChild(_root);
            AddLogicalChild(_root);
        }

        public static readonly DependencyProperty MarkdownProperty =
            DependencyProperty.Register(
                nameof(Markdown),
                typeof(string),
                typeof(MarkdownPresenter),
                new FrameworkPropertyMetadata(string.Empty, OnMarkdownChanged));

        public string Markdown
        {
            get => (string)GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((MarkdownPresenter)d).Rebuild((string?)e.NewValue ?? string.Empty);

        // ----- visual-tree plumbing (single managed child) --------------------------------------

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _root;
        protected override Size MeasureOverride(Size constraint)
        {
            _root.Measure(constraint);
            return _root.DesiredSize;
        }
        protected override Size ArrangeOverride(Size arrangeBounds)
        {
            _root.Arrange(new Rect(arrangeBounds));
            return arrangeBounds;
        }

        // ----- parsing --------------------------------------------------------------------------

        private void Rebuild(string md)
        {
            _root.Children.Clear();
            if (string.IsNullOrEmpty(md)) return;

            try
            {
                var lines = md.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                int i = 0;
                while (i < lines.Length)
                {
                    string line = lines[i];

                    // Fenced code block.
                    if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        var code = new StringBuilder();
                        i++;
                        while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                        {
                            code.AppendLine(lines[i]);
                            i++;
                        }
                        i++; // skip closing fence
                        _root.Children.Add(BuildCodeBlock(code.ToString().TrimEnd('\n')));
                        continue;
                    }

                    // Heading.
                    if (line.StartsWith("#", StringComparison.Ordinal))
                    {
                        int level = 0;
                        while (level < line.Length && line[level] == '#') level++;
                        string text = line.Substring(level).Trim();
                        _root.Children.Add(BuildHeading(text, level));
                        i++;
                        continue;
                    }

                    // Bullet / numbered list (group consecutive items).
                    if (IsBullet(line))
                    {
                        var items = new List<string>();
                        while (i < lines.Length && IsBullet(lines[i]))
                        {
                            items.Add(StripBullet(lines[i]));
                            i++;
                        }
                        _root.Children.Add(BuildList(items));
                        continue;
                    }

                    // Blank line = paragraph break.
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        i++;
                        continue;
                    }

                    // Paragraph: gather consecutive non-blank, non-special lines.
                    var para = new StringBuilder(line);
                    i++;
                    while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])
                           && !IsBullet(lines[i])
                           && !lines[i].StartsWith("#", StringComparison.Ordinal)
                           && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        para.Append(' ').Append(lines[i].Trim());
                        i++;
                    }
                    _root.Children.Add(BuildParagraph(para.ToString()));
                }
            }
            catch
            {
                // Total fallback: never lose the message to a parser edge case.
                _root.Children.Clear();
                _root.Children.Add(BuildParagraph(md));
            }
        }

        private static bool IsBullet(string line)
        {
            var t = line.TrimStart();
            if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
                return true;
            // numbered "1. "
            int j = 0;
            while (j < t.Length && char.IsDigit(t[j])) j++;
            return j > 0 && j + 1 < t.Length && t[j] == '.' && t[j + 1] == ' ';
        }

        private static string StripBullet(string line)
        {
            var t = line.TrimStart();
            if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
                return t.Substring(2);
            int j = 0;
            while (j < t.Length && char.IsDigit(t[j])) j++;
            if (j > 0 && j + 1 < t.Length && t[j] == '.') return t.Substring(j + 2);
            return t;
        }

        // ----- block builders -------------------------------------------------------------------

        private TextBlock BuildParagraph(string text)
        {
            var tb = NewTextBlock();
            tb.Margin = new Thickness(0, 0, 0, 4);
            AddInlineRuns(tb.Inlines, text);
            return tb;
        }

        private TextBlock BuildHeading(string text, int level)
        {
            var tb = NewTextBlock();
            tb.FontWeight = FontWeights.SemiBold;
            tb.FontSize = level <= 1 ? 16 : level == 2 ? 14 : 13;
            tb.Margin = new Thickness(0, 2, 0, 4);
            AddInlineRuns(tb.Inlines, text);
            return tb;
        }

        private FrameworkElement BuildList(IEnumerable<string> items)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var item in items)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                var bullet = NewTextBlock();
                bullet.Text = "•  ";
                bullet.VerticalAlignment = VerticalAlignment.Top;
                var content = NewTextBlock();
                content.TextWrapping = TextWrapping.Wrap;
                AddInlineRuns(content.Inlines, item);
                row.Children.Add(bullet);
                row.Children.Add(content);
                panel.Children.Add(row);
            }
            return panel;
        }

        private FrameworkElement BuildCodeBlock(string code)
        {
            var tb = new TextBox
            {
                Text = code,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                Background = Brushes.Transparent,
                Foreground = TextBrush(),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            return new Border
            {
                Background = SurfaceAltBrush(),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 2, 0, 6),
                Child = tb
            };
        }

        // ----- inline (bold / italic / code) ----------------------------------------------------

        private void AddInlineRuns(InlineCollection target, string text)
        {
            // A single forward pass that toggles bold/italic on **/*/_ and code on `, emitting runs.
            int i = 0;
            var buffer = new StringBuilder();
            void Flush(Action<Run>? decorate = null)
            {
                if (buffer.Length == 0) return;
                var run = new Run(buffer.ToString()) { Foreground = TextBrush() };
                decorate?.Invoke(run);
                target.Add(run);
                buffer.Clear();
            }

            while (i < text.Length)
            {
                // inline code
                if (text[i] == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        Flush();
                        var code = text.Substring(i + 1, end - i - 1);
                        target.Add(new Run(code)
                        {
                            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                            Background = SurfaceAltBrush(),
                            Foreground = TextBrush()
                        });
                        i = end + 1;
                        continue;
                    }
                }

                // bold (**) or (__)
                if (i + 1 < text.Length && (text[i] == '*' || text[i] == '_') && text[i + 1] == text[i])
                {
                    char c = text[i];
                    int end = text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                    if (end > i)
                    {
                        Flush();
                        var inner = text.Substring(i + 2, end - i - 2);
                        target.Add(new Run(inner) { FontWeight = FontWeights.SemiBold, Foreground = TextBrush() });
                        i = end + 2;
                        continue;
                    }
                }

                // italic (* or _)
                if ((text[i] == '*' || text[i] == '_'))
                {
                    char c = text[i];
                    int end = text.IndexOf(c, i + 1);
                    if (end > i)
                    {
                        Flush();
                        var inner = text.Substring(i + 1, end - i - 1);
                        target.Add(new Run(inner) { FontStyle = FontStyles.Italic, Foreground = TextBrush() });
                        i = end + 1;
                        continue;
                    }
                }

                buffer.Append(text[i]);
                i++;
            }
            Flush();
        }

        // ----- helpers --------------------------------------------------------------------------

        private TextBlock NewTextBlock() => new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextBrush(),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13
        };

        private Brush TextBrush() =>
            TryFindResource("Brush.Text") as Brush ?? Brushes.White;

        private Brush SurfaceAltBrush() =>
            TryFindResource("Brush.SurfaceAlt") as Brush ?? new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x36));
    }
}
