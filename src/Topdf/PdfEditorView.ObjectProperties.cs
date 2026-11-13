using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Topdf;

public partial class PdfEditorView
{
    bool propertiesDismissed;
    string? propertiesFingerprint;
    int propertiesRevision;

    internal bool IsObjectPropertiesOpen => ObjectPropertiesPane.Visibility == Visibility.Visible;

    internal void ToggleObjectProperties()
    {
        propertiesDismissed = IsObjectPropertiesOpen;
        SetObjectPropertiesVisibility(!propertiesDismissed);
        RefreshObjectProperties();
    }

    void CloseObjectProperties(object sender, RoutedEventArgs e)
    {
        propertiesDismissed = true;
        SetObjectPropertiesVisibility(false);
        Keyboard.Focus(CanEditSpread ? SpreadPages : EditCanvas);
    }

    void SetObjectPropertiesVisibility(bool open)
    {
        if (IsObjectPropertiesOpen == open) return;
        var viewport = CaptureCanvasViewport();
        ObjectPropertiesPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (viewport != null) RestoreCanvasViewport(viewport);
    }

    void RefreshObjectProperties()
    {
        if (ObjectPropertiesPane == null || info == null) return;
        var items = (!IsViewing || CanEditSpread) && Tool == "선택" && selectionBox == null ? Objects() : [];
        if (items.Count > 0 && !propertiesDismissed && !IsObjectPropertiesOpen)
            SetObjectPropertiesVisibility(true);
        if (!IsObjectPropertiesOpen) return;

        var fingerprint = JsonSerializer.Serialize(items.Select(x => new {
            x.Id, x.Kind, x.NoteRegion, x.Color, x.Fill, x.FillUsesStrokeColor, x.FillOpacity, x.StrokeWidth, Line = EffectiveLineStyle(x),
            x.FontFamily, x.FontSize, x.Alignment, x.Width, x.Height, x.Rotation,
            x.TextBoxFill,x.TextBoxBorderColor,x.TextBoxBorderLineStyle,x.TextBoxBorderWidth,x.ImageOpacity,x.LockImageAspectRatio
        }));
        if (fingerprint == propertiesFingerprint) return;
        propertiesFingerprint = fingerprint;
        var revision = ++propertiesRevision;
        ObjectPropertiesFields.Children.Clear();

        if (items.Count == 0)
        {
            ObjectPropertiesFields.Children.Add(new TextBlock { Text = "선택한 객체가 없습니다.", Foreground = System.Windows.Media.Brushes.DimGray });
            return;
        }

        ObjectPropertiesFields.Children.Add(new TextBlock {
            Text = items.Count == 1 ? ObjectKindLabel(items[0].Kind) : $"객체 {items.Count}개 선택",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4)
        });
        if (items.Count == 1 && items[0].NoteRegion is { } region)
        {
            var origin = plan.Pages.FindIndex(page => page.SourceIndex == region.SourceIndex);
            var jump = new Button {
                Content = origin < 0 ? "원래 영역의 페이지가 삭제됨" : $"원래 영역 · {origin + 1}쪽",
                IsEnabled = origin >= 0, MinHeight = 25
            };
            jump.Click += async (_, _) => {
                var index = plan.Pages.FindIndex(page => page.SourceIndex == region.SourceIndex);
                if (index >= 0) await NavigateToPageAsync(index + 1);
            };
            ObjectPropertiesFields.Children.Add(jump);
        }

        void Choice(string label, Func<PdfEditElement, string> read, Action<PdfEditElement, string> write,
            (string Value, string Label)[] choices, Func<string, FrameworkElement?>? preview = null)
        {
            var values = items.Select(read).Distinct().ToArray();
            var options = new List<(string Value, string Label)>(choices);
            options.AddRange(values.Where(value => choices.All(option => option.Value != value)).Select(value => (value, value)));
            var box = new ComboBox { MinHeight = 25 };
            if (values.Length > 1) box.Items.Add(new ComboBoxItem { Content = "혼합", IsEnabled = false });
            foreach (var (value, title) in options)
                box.Items.Add(new ComboBoxItem {
                    Content = preview?.Invoke(value) is {} sample ? PropertyChoicePreview(sample, title) : title,
                    Tag = value
                });
            box.SelectedItem = values.Length > 1 ? box.Items[0]
                : box.Items.OfType<ComboBoxItem>().FirstOrDefault(option => Equals(option.Tag, values[0]));
            box.SelectionChanged += (_, _) => {
                if (revision == propertiesRevision && box.SelectedItem is ComboBoxItem { Tag: string value })
                    ApplyDockedProperty(read, write, value);
            };
            AddPropertyField(label, box);
        }

        void Number(string label, Func<PdfEditElement, double> read, Action<PdfEditElement, double> write,
            double min, double max, string unit = "pt")
        {
            var values = items.Select(read).Distinct().ToArray();
            var original = values.Length == 1 && double.IsFinite(values[0]) ? values[0].ToString("0.##", CultureInfo.CurrentCulture) : "";
            var box = new TextBox { Text = original, MinHeight = 25, MaxLength = 12, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = values.Length > 1 ? "혼합 값 · 입력 후 Enter" : "입력 후 Enter" };
            void Apply()
            {
                if (revision != propertiesRevision || box.Text == original || string.IsNullOrWhiteSpace(box.Text)) return;
                if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
                    double.IsFinite(value) && value >= min && value <= max)
                    ApplyDockedProperty(read, write, value);
                else
                {
                    box.Text = original;
                    ShowPageOperationError($"{label} 값은 {min:0.##}~{max:0.##} {unit} 범위로 입력하세요.");
                }
            }
            box.LostKeyboardFocus += (_, _) => Apply();
            box.PreviewKeyDown += (_, e) => {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                Apply();
                Keyboard.Focus(EditCanvas);
            };
            AddPropertyField(label, box);
        }

        (string Value,string Label)[] colors = [("#FF6500", "주황"), ("#111111", "검정"), ("#E51B23", "빨강"),
            ("#FFB900", "노랑"), ("#16804A", "초록"), ("#1755B5", "파랑"), ("#7B3FB3", "보라"),
            ("#808080", "회색"), ("#FFFFFF", "흰색")];

        if (items.All(x => x.Kind != "stamp"))
            Choice("색상", x => x.Color, (x, color) => {
                x.Color = color;
                if (x.Kind == "replace") x.PreserveSourceStyle = false;
            }, colors, PropertyColorSample);

        (string Value, string Label)[] lineStyles = [("solid", "실선"), ("dotted", "점선"), ("dashed", "파선"), ("dashdot", "일점쇄선"), ("dashdotdot", "이점쇄선")];
        if (items.All(x => Graphics.Contains(x.Kind)))
        {
            Number("선 두께 (pt)", x => x.StrokeWidth, (x, value) => x.StrokeWidth = value, 1, 8);
            Choice("선 종류", EffectiveLineStyle, (x, value) => { x.LineStyle = value; x.Dashed = value == "dashed"; },
                lineStyles, PropertyLineSample);
        }
        if (items.All(x => x.Kind is "rectangle" or "ellipse"))
        {
            Choice("채움 색상", x => x.FillUsesStrokeColor && x.Fill!="none" ? "stroke" : x.Fill,
                (x, value) => {
                    x.FillUsesStrokeColor = value=="stroke";
                    x.Fill = x.FillUsesStrokeColor ? x.Color : value;
                }, [("none", "없음"), ("stroke", "테두리 색"), ..colors], PropertyColorSample);
            if(items.All(x=>x.Fill!="none"))
                AddFillOpacityField("채움 불투명도 (%)", items, revision);
            var size = SizeOfPage();
            var maxDimension = Math.Max(size.W, size.H);
            Number("너비 (pt)", x => x.Width, (x, value) => x.Width = value, 1, maxDimension);
            Number("높이 (pt)", x => x.Height, (x, value) => x.Height = value, 1, maxDimension);
            if(items.All(x=>x.Kind=="ellipse"))
                Number("반지름 (pt)", x => Math.Abs(x.Width-x.Height)<.000001 ? x.Width/2 : double.NaN,
                    (x, radius) => {
                        x.X += x.Width/2-radius;
                        x.Y += x.Height/2-radius;
                        x.Width=x.Height=radius*2;
                    }, .5, Math.Min(size.W,size.H)/2);
        }

        if (items.All(x => x.Kind is "text" or "note" or "replace"))
        {
            Choice("글꼴", x => x.FontFamily, (x, value) => {
                x.FontFamily = value;
                x.PreserveSourceStyle = false;
                ResizePropertyText(x);
            }, [("Malgun Gothic", "맑은 고딕"), ("Gulim", "굴림"), ("Batang", "바탕")]);
            Number("글자 크기 (pt)", x => x.FontSize, (x, value) => { x.FontSize = value; ResizePropertyText(x); }, 1, 200);
            Choice("정렬", x => x.Alignment, (x, value) => {
                x.Alignment = value;
                if (value != "left") x.PreserveSourceStyle = false;
                ResizePropertyText(x);
            }, [("left", "왼쪽"), ("center", "가운데"), ("right", "오른쪽")]);
        }
        if(items.All(x=>x.Kind=="text")){
            Choice("글상자 테두리 색상",x=>x.TextBoxBorderColor,(x,value)=>x.TextBoxBorderColor=value,
                [("none","없음"),..colors],PropertyColorSample);
            if(items.All(x=>x.TextBoxBorderColor!="none")) {
                Number("글상자 테두리 두께 (pt)",x=>x.TextBoxBorderWidth,(x,value)=>x.TextBoxBorderWidth=value,.5,8);
                Choice("글상자 테두리 선 종류", x => x.TextBoxBorderLineStyle,
                    (x, value) => x.TextBoxBorderLineStyle = value, lineStyles, PropertyLineSample);
            }
            Choice("글상자 채움 색상",x=>x.TextBoxFill,(x,value)=>x.TextBoxFill=value,
                [("none","없음"),..colors],PropertyColorSample);
            if(items.All(x=>x.TextBoxFill!="none"))
                AddFillOpacityField("글상자 채움 불투명도 (%)", items, revision);
        }
        if (items.All(x => x.Kind == "stamp"))
        {
            AddStampOpacityField(items, revision);
            if (items.Count == 1) {
                var editBackground = new Button { Content = "배경 제거…", Margin = new Thickness(0, 6, 0, 0) };
                editBackground.Click += (_, _) => EditStampBackground(items[0]);
                ObjectPropertiesFields.Children.Add(editBackground);
            }
            var aspectValues = items.Select(item => item.LockImageAspectRatio).Distinct().ToArray();
            var lockRatio = new CheckBox { IsChecked = aspectValues.Length == 1 ? aspectValues[0] : null,
                Content = "가로세로 비율 고정", Margin = new Thickness(0, 6, 0, 0) };
            lockRatio.Click += (_, _) => {
                if (revision == propertiesRevision)
                    ApplyDockedProperty(item => item.LockImageAspectRatio,
                        (item, locked) => item.LockImageAspectRatio = locked, lockRatio.IsChecked == true, updateSpread: false);
            };
            ObjectPropertiesFields.Children.Add(lockRatio);
            var size = SizeOfPage();
            Number("너비 (pt)", x => x.Width, (x, value) => { if(x.LockImageAspectRatio)x.Height *= value / x.Width; x.Width = value; }, 1, Math.Max(size.W, size.H));
            Number("높이 (pt)", x => x.Height, (x, value) => { if(x.LockImageAspectRatio)x.Width *= value / x.Height; x.Height = value; }, 1, Math.Max(size.W, size.H));
            Choice("회전", x => x.Rotation.ToString(CultureInfo.InvariantCulture),
                (x, value) => x.Rotation = int.Parse(value, CultureInfo.InvariantCulture),
                [("0", "0°"), ("90", "90°"), ("180", "180°"), ("270", "270°")]);
        }
        if (items.All(item => item.Kind != "replace") && !CanEditSpread)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (label, handler) in new (string, RoutedEventHandler)[] {
                ("회전", RotateObjects), ("맨 앞", BringForward), ("맨 뒤", SendBackward)
            })
            {
                var button = new Button { Content = label, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 4) };
                button.Click += handler;
                actions.Children.Add(button);
            }
            ObjectPropertiesFields.Children.Add(actions);
        }
        if (ObjectPropertiesFields.Children.Count == 1)
            ObjectPropertiesFields.Children.Add(new TextBlock { Text = "공통 편집 속성이 없습니다.", TextWrapping = TextWrapping.Wrap });
    }

    static FrameworkElement PropertyChoicePreview(FrameworkElement sample, string title)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        sample.VerticalAlignment = VerticalAlignment.Center;
        sample.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(sample);
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    static FrameworkElement? PropertyColorSample(string value)
    {
        if (!value.StartsWith("#") || ColorConverter.ConvertFromString(value) is not Color color) return null;
        return new Rectangle {
            Width = 16, Height = 13, RadiusX = 2, RadiusY = 2,
            Fill = new SolidColorBrush(color),
            Stroke = Brushes.SlateGray, StrokeThickness = 1
        };
    }

    static FrameworkElement PropertyLineSample(string value) => new Line {
        Width = 62, Height = 12, X1 = 0, X2 = 62, Y1 = 6, Y2 = 6,
        Stroke = Brushes.DarkSlateGray, StrokeThickness = 1.5,
        StrokeDashArray = LineDashArray(new PdfEditElement { LineStyle = value })
    };

    void AddPropertyField(string label, FrameworkElement control)
    {
        var field = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
        AutomationProperties.SetName(control, label);
        field.Children.Add(control);
        ObjectPropertiesFields.Children.Add(field);
    }

    async void ApplyDockedProperty<T>(Func<PdfEditElement, T> read, Action<PdfEditElement, T> write, T value, bool updateSpread = true)
    {
        if ((IsViewing&&!CanEditSpread) || IsBusy || !IsObjectPropertiesOpen || Tool != "선택") return;
        var changed = Objects().Where(item => !EqualityComparer<T>.Default.Equals(read(item), value)).ToArray();
        if (changed.Length == 0) return;
        var before = Capture();
        try
        {
            foreach (var item in changed)
            {
                write(item, value);
                Clamp(item);
            }
            SyncRibbonObjectProperties(Selected);
            Commit(before,keepSpread:CanEditSpread);
            if(CanEditSpread) {
                if(updateSpread)await RefreshSpreadAfterTransformAsync(changed.ToList());
                else { PreviewStampOpacity(changed,null); RefreshObjectProperties(); }
            }
            else if (changed.Any(x => x.Kind == "replace")) _ = RenderPageAsync();
        }
        catch (Exception ex)
        {
            propertiesFingerprint = null;
            Restore(before);
            ShowPageOperationError(ex.Message);
        }
    }

    void ResizePropertyText(PdfEditElement item)
    {
        if (item.Kind == "replace") item.Height = Math.Max(item.Height, TextHeight(item));
        else FitTextBox(item, item.Text);
    }

    static string ObjectKindLabel(string kind) => kind switch
    {
        "line" => "선", "arrow" => "화살표", "rectangle" => "사각형", "ellipse" => "타원",
        "highlight" => "형광펜", "underline" => "밑줄", "strikeout" => "취소선", "freehand" => "자유곡선",
        "text" => "글자", "note" => "메모", "replace" => "본문", "stamp" => "도장",
        _ => "객체"
    };
}
