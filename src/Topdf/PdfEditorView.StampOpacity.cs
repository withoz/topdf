using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Topdf;

public partial class PdfEditorView
{
    Action? commitPendingStampOpacity;

    void AddStampOpacityField(IReadOnlyList<PdfEditElement> items, int revision)
    {
        const string label = "그림 투명도 (%)";
        var row = new DockPanel { LastChildFill = true };
        var values = items.Select(StampTransparency).Distinct().ToArray();
        var slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10,
            IsMoveToPointEnabled = true, Value = values[0], VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "0%는 원래 표시, 100%는 완전히 투명" };
        var input = new TextBox { Width = 52, MinHeight = 25, Margin = new Thickness(8, 0, 0, 0),
            Text = values.Length == 1 ? Format(values[0]) : "", MaxLength = 6,
            ToolTip = values.Length > 1 ? "혼합 값 · 0~100 입력" : "0~100 입력",
            VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, label + " 슬라이더");
        AutomationProperties.SetName(input, label);
        DockPanel.SetDock(input, Dock.Right);
        row.Children.Add(input);
        row.Children.Add(slider);
        AddPropertyField(label, row);

        var syncing = false;
        var dragging = false;
        double? pending = null;
        static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

        void Show(double value)
        {
            pending = value;
            commitPendingStampOpacity = CommitOpacity;
            PreviewStampOpacity(items, 1-value/100);
        }

        void CommitOpacity()
        {
            if (commitPendingStampOpacity == CommitOpacity) commitPendingStampOpacity = null;
            if (revision != propertiesRevision || pending is not double value) return;
            pending = null;
            ApplyDockedProperty(StampTransparency, (item, amount) => item.ImageOpacity = 1-amount/100, value, updateSpread: false);
        }

        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) => {
            dragging = false;
            if (!e.Canceled) CommitOpacity();
            else {
                pending = null;
                if (commitPendingStampOpacity == CommitOpacity) commitPendingStampOpacity = null;
                PreviewStampOpacity(items, null);
                syncing = true;
                slider.Value = values[0];
                input.Text = values.Length == 1 ? Format(values[0]) : "";
                syncing = false;
            }
        }));
        slider.ValueChanged += (_, _) => {
            if (syncing || revision != propertiesRevision) return;
            var value = Math.Round(slider.Value, 2);
            syncing = true;
            input.Text = Format(value);
            syncing = false;
            Show(value);
            if (!dragging) CommitOpacity();
        };
        input.TextChanged += (_, _) => {
            if (syncing || revision != propertiesRevision) return;
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
                !double.IsFinite(value) || value < 0 || value > 100) {
                pending = null;
                if (commitPendingStampOpacity == CommitOpacity) commitPendingStampOpacity = null;
                PreviewStampOpacity(items, null);
                return;
            }
            syncing = true;
            slider.Value = value;
            syncing = false;
            Show(value);
        };
        row.LostKeyboardFocus += (_, e) => {
            if (e.NewFocus is DependencyObject next && row.IsAncestorOf(next)) return;
            CommitOpacity();
        };
        input.PreviewKeyDown += (_, e) => {
            if (e.Key != Key.Enter) return;
            CommitOpacity();
            e.Handled = true;
        };
    }

    static double StampTransparency(PdfEditElement item) => Math.Round((1-item.ImageOpacity)*100, 2);

    void PreviewStampOpacity(IReadOnlyList<PdfEditElement> items, double? opacity)
    {
        var byId = items.ToDictionary(item => item.Id);
        foreach (var border in EditCanvas.Children.OfType<AnnotationBorder>())
            if (border.Tag is PdfEditElement item && byId.TryGetValue(item.Id, out var selected) && border.Child is Image image)
                image.Opacity = opacity ?? selected.ImageOpacity;
        if (spreadSelectionOverlay is { } overlay)
            foreach (var image in overlay.Children.OfType<Image>())
                if (image.Tag is PdfEditElement item && byId.TryGetValue(item.Id, out var selected))
                    image.Opacity = opacity ?? selected.ImageOpacity;
    }
}
