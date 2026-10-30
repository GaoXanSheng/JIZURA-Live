using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage;

internal static class ColorPickerPopup
{
    private static Popup? _open;

    public static void Show(FrameworkElement target, string initialHex, Action<string> commit,
        Action<string>? preview = null)
    {
        _open?.SetCurrentValue(Popup.IsOpenProperty, false);

        var picker = new Picker(initialHex, commit, preview);
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = picker.Root,
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, popup)) _open = null;
        };
        _open = popup;
        popup.IsOpen = true;
    }

    private sealed class Picker
    {
        private const double SvWidth = 196;
        private const double SvHeight = 144;
        private const double HueWidth = 14;

        private readonly Action<string> _commit;
        private readonly Action<string>? _preview;
        private readonly SolidColorBrush _hueBase = new(Colors.Red);
        private readonly Border _svThumb;
        private readonly Border _hueThumb;
        private readonly TextBox _hexBox;
        private readonly Border _previewSwatch;
        private double _h;
        private double _s;
        private double _v;
        private bool _syncing;

        public FrameworkElement Root { get; }

        public Picker(string initialHex, Action<string> commit, Action<string>? preview)
        {
            _commit = commit;
            _preview = preview;
            Parse(initialHex, out _h, out _s, out _v);

            var sv = new Grid
            {
                Width = SvWidth,
                Height = SvHeight,
                ClipToBounds = true,
                Cursor = Cursors.Cross,
                Background = _hueBase,
            };
            sv.Children.Add(new Rectangle
            {
                Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
            });
            sv.Children.Add(new Rectangle
            {
                Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
            });

            var svCanvas = new Canvas { IsHitTestVisible = false };
            _svThumb = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                Background = Brushes.Transparent,
            };
            svCanvas.Children.Add(_svThumb);
            sv.Children.Add(svCanvas);

            var hue = new Canvas
            {
                Width = HueWidth,
                Height = SvHeight,
                ClipToBounds = true,
                Cursor = Cursors.Hand,
            };
            hue.Children.Add(new Rectangle
            {
                Width = HueWidth,
                Height = SvHeight,
                RadiusX = 4,
                RadiusY = 4,
                Fill = HueBrush(),
            });
            _hueThumb = new Border
            {
                Width = HueWidth + 4,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 0, 0)),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
            };
            hue.Children.Add(_hueThumb);

            _hexBox = new TextBox
            {
                Text = Hex(),
                FontSize = 12,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"),
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = L.T("StageUi.Media.HexColorInputHint"),
            };
            _hexBox.TextChanged += (_, _) =>
            {
                if (_syncing || !TryParse(_hexBox.Text, out var color)) return;
                (_h, _s, _v) = RgbToHsv(color);
                UpdateHueBase();
                UpdateThumbs();
                UpdatePreview(syncHex: false);
                Preview();
            };
            _hexBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                Commit();
                e.Handled = true;
            };
            _hexBox.LostFocus += (_, _) => Commit();

            _previewSwatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(CurrentColor()),
            };
            _previewSwatch.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

            var label = new TextBlock
            {
                Text = "HEX",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

            var hexRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 0);
            Grid.SetColumn(_hexBox, 1);
            Grid.SetColumn(_previewSwatch, 2);
            hexRow.Children.Add(label);
            hexRow.Children.Add(_hexBox);
            hexRow.Children.Add(_previewSwatch);

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(sv);
            top.Children.Add(new Border { Width = 10 });
            top.Children.Add(hue);

            var panel = new StackPanel();
            panel.Children.Add(top);
            panel.Children.Add(hexRow);

            var root = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Child = panel,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 22,
                    ShadowDepth = 4,
                    Direction = 270,
                    Opacity = 0.28,
                    Color = Colors.Black,
                },
            };
            root.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            root.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            Root = root;

            sv.MouseLeftButtonDown += (_, e) =>
            {
                sv.CaptureMouse();
                SetSv(e.GetPosition(sv));
                e.Handled = true;
            };
            sv.MouseMove += (_, e) =>
            {
                if (sv.IsMouseCaptured) SetSv(e.GetPosition(sv));
            };
            sv.MouseLeftButtonUp += (_, e) =>
            {
                if (!sv.IsMouseCaptured) return;
                sv.ReleaseMouseCapture();
                Commit();
                e.Handled = true;
            };

            hue.MouseLeftButtonDown += (_, e) =>
            {
                hue.CaptureMouse();
                SetHue(e.GetPosition(hue).Y);
                e.Handled = true;
            };
            hue.MouseMove += (_, e) =>
            {
                if (hue.IsMouseCaptured) SetHue(e.GetPosition(hue).Y);
            };
            hue.MouseLeftButtonUp += (_, e) =>
            {
                if (!hue.IsMouseCaptured) return;
                hue.ReleaseMouseCapture();
                Commit();
                e.Handled = true;
            };

            UpdateHueBase();
            UpdateThumbs();
        }

        private void SetSv(Point p)
        {
            _s = Math.Clamp(p.X / SvWidth, 0, 1);
            _v = 1 - Math.Clamp(p.Y / SvHeight, 0, 1);
            UpdateThumbs();
            UpdatePreview();
            Preview();
        }

        private void SetHue(double y)
        {
            _h = Math.Clamp(y / SvHeight, 0, 1) * 360;
            UpdateHueBase();
            UpdateThumbs();
            UpdatePreview();
            Preview();
        }

        private void UpdateHueBase() => _hueBase.Color = HsvToRgb(_h, 1, 1);

        private void UpdateThumbs()
        {
            Canvas.SetLeft(_svThumb, _s * SvWidth - _svThumb.Width / 2);
            Canvas.SetTop(_svThumb, (1 - _v) * SvHeight - _svThumb.Height / 2);
            Canvas.SetLeft(_hueThumb, (HueWidth - _hueThumb.Width) / 2);
            Canvas.SetTop(_hueThumb, _h / 360 * SvHeight - _hueThumb.Height / 2);
        }

        private void UpdatePreview(bool syncHex = true)
        {
            var color = CurrentColor();
            _previewSwatch.Background = new SolidColorBrush(color);
            if (!syncHex) return;

            _syncing = true;
            _hexBox.Text = Hex(color);
            _syncing = false;
        }

        private void Preview() => _preview?.Invoke(Hex());

        private void Commit() => _commit(Hex());

        private Color CurrentColor() => HsvToRgb(_h, _s, _v);

        private string Hex() => Hex(CurrentColor());

        private static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

        private static void Parse(string text, out double h, out double s, out double v)
        {
            var color = TryParse(text, out var parsed) ? parsed : Colors.White;
            (h, s, v) = RgbToHsv(color);
        }

        private static bool TryParse(string? text, out Color color)
        {
            color = Colors.White;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var t = text.Trim().TrimStart('#');
            if (t.Length == 3)
                t = new string([t[0], t[0], t[1], t[1], t[2], t[2]]);
            if (t.Length != 6) return false;
            if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;

            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }

        private static (double H, double S, double V) RgbToHsv(Color color)
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            double h = 0;
            if (d > 1e-6)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * ((b - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
            }

            if (h < 0) h += 360;
            return (h, max <= 0 ? 0 : d / max, max);
        }

        private static Color HsvToRgb(double h, double s, double v)
        {
            h = (h % 360 + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            double m = v - c;

            (double r, double g, double b) = h switch
            {
                < 60 => (c, x, 0d),
                < 120 => (x, c, 0d),
                < 180 => (0d, c, x),
                < 240 => (0d, x, c),
                < 300 => (x, 0d, c),
                _ => (c, 0d, x),
            };

            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        private static LinearGradientBrush HueBrush()
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            brush.GradientStops.Add(new GradientStop(Colors.Red, 0));
            brush.GradientStops.Add(new GradientStop(Colors.Yellow, 1.0 / 6));
            brush.GradientStops.Add(new GradientStop(Colors.Lime, 2.0 / 6));
            brush.GradientStops.Add(new GradientStop(Colors.Cyan, 3.0 / 6));
            brush.GradientStops.Add(new GradientStop(Colors.Blue, 4.0 / 6));
            brush.GradientStops.Add(new GradientStop(Colors.Magenta, 5.0 / 6));
            brush.GradientStops.Add(new GradientStop(Colors.Red, 1));
            return brush;
        }
    }
}
