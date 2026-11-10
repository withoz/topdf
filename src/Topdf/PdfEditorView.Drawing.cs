using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
namespace Topdf;
public partial class PdfEditorView
{
    string cleanState="";
    void MarkClean(){cleanState=JsonSerializer.Serialize(plan,ProjectJson);dirty=false;}
    bool IsClean()=>cleanState==JsonSerializer.Serialize(plan,ProjectJson);
    readonly HashSet<string> selectedIds = [];
    Dictionary<string,Point> groupOrigins = [];
    List<PdfEditElement> copied = [];
    PdfEditElement? drawing;
    History? drawingBefore;
    Point drawingStart;
    static readonly HashSet<string> Graphics = ["line","arrow","rectangle","ellipse","highlight","underline","strikeout","freehand"];
    static TextAlignment AlignmentOf(PdfEditElement x) => x.Alignment == "center" ? TextAlignment.Center : x.Alignment == "right" ? TextAlignment.Right : TextAlignment.Left;
    List<PdfEditElement> Objects() => Page?.Elements.Where(x=>selectedIds.Contains(x.Id) || x.Id==selectedId).ToList() ?? [];
    void SetObjectStyle(PdfEditElement x) { x.Color=ColorValue; x.FontSize=FontSizeValue; x.StrokeWidth=double.Parse(((ComboBoxItem)StrokeSize.SelectedItem).Content.ToString()!); x.LineStyle=CurrentLineStyle;x.Dashed=x.LineStyle=="dashed";x.Fill=FillShape.IsChecked==true?ColorValue:"none";var family=((ComboBoxItem)TextFont.SelectedItem).Content.ToString()!;if(family!=x.FontFamily)x.PreserveSourceStyle=false;x.FontFamily=family;x.Alignment=new[]{"left","center","right"}[TextAlign.SelectedIndex]; }
    bool ShowSelectionDecorations=>drawing==null && EditTool?.SelectedIndex is 0 or 1;
    void RefreshSelectionDecorations()
    {
        foreach(var border in EditCanvas.Children.OfType<Border>().Where(x=>x.Tag is PdfEditElement)){
            var item=(PdfEditElement)border.Tag;border.BorderThickness=new Thickness(ShowSelectionDecorations&&(selectedId==item.Id||selectedIds.Contains(item.Id))?1:0);
        }
        if(!ShowSelectionDecorations){foreach(var thumb in EditCanvas.Children.OfType<System.Windows.Controls.Primitives.Thumb>().ToArray())EditCanvas.Children.Remove(thumb);}
        else if(!EditCanvas.Children.OfType<System.Windows.Controls.Primitives.Thumb>().Any()){DrawResizeHandle();UpdateZoomAdorners();}
    }
    bool DrawGraphic(PdfEditElement x)
    {
        if(!Graphics.Contains(x.Kind))return false;
        var canvas=new Canvas { Width=x.Width, Height=x.Height, ClipToBounds=false };
        var brush=(Brush)new BrushConverter().ConvertFromString(x.Color)!;
        var inset=Math.Min(x.StrokeWidth/2,Math.Min(x.Width,x.Height)/2);
        Shape shape;
        if(x.Kind is "rectangle" or "highlight") shape=new Rectangle { Width=Math.Max(.1,x.Width-2*inset),Height=Math.Max(.1,x.Height-2*inset) };
        else if(x.Kind=="ellipse") shape=new Ellipse { Width=Math.Max(.1,x.Width-2*inset),Height=Math.Max(.1,x.Height-2*inset) };
        else {
            var points=x.Points.Count>0 ? x.Points : new List<double[]> {new[]{0.0,0.0},new[]{1.0,1.0}};
            var path=new Polyline { Points=new PointCollection(points.Select(p=>new Point(inset+p[0]*(x.Width-2*inset),inset+p[1]*(x.Height-2*inset)))) };
            shape=path;
        }
        shape.Stroke=brush;shape.StrokeThickness=x.StrokeWidth;shape.Fill=x.Kind is "rectangle" or "ellipse" ? (x.Fill=="none"?Brushes.Transparent:(Brush)new BrushConverter().ConvertFromString(x.Fill)!) : null;
        shape.StrokeDashArray=LineDashArray(x);
        if(x.Kind=="highlight") {shape.Fill=brush;shape.Stroke=Brushes.Transparent;shape.Opacity=.3;}
        if(x.Kind is "rectangle" or "ellipse" or "highlight") {Canvas.SetLeft(shape,inset);Canvas.SetTop(shape,inset);}
        canvas.Children.Add(shape);
        if(x.Kind=="arrow" && x.Points.Count>=2) {
            var a=x.Points[^2];var b=x.Points[^1];var end=new Point(inset+b[0]*(x.Width-2*inset),inset+b[1]*(x.Height-2*inset));
            var angle=Math.Atan2((b[1]-a[1])*(x.Height-2*inset),(b[0]-a[0])*(x.Width-2*inset));var length=Math.Min(Math.Min(x.Width,x.Height)/2,Math.Max(6,x.StrokeWidth*4));
            canvas.Children.Add(new Polyline { Stroke=brush,StrokeThickness=x.StrokeWidth,Points=new PointCollection{new(end.X-length*Math.Cos(angle-.5),end.Y-length*Math.Sin(angle-.5)),end,new(end.X-length*Math.Cos(angle+.5),end.Y-length*Math.Sin(angle+.5))} });
        }
        var border=new Border {Width=x.Width,Height=x.Height,Child=canvas,Background=Brushes.Transparent,IsHitTestVisible=false,Tag=x,RenderTransformOrigin=new(.5,.5),RenderTransform=new RotateTransform(x.Rotation),BorderBrush=Brushes.DodgerBlue,BorderThickness=new Thickness(ShowSelectionDecorations&&(selectedIds.Contains(x.Id)||selectedId==x.Id)?1:0)};
        border.MouseLeftButtonDown+=ElementMouseDown;Canvas.SetLeft(border,x.X);Canvas.SetTop(border,x.Y);EditCanvas.Children.Add(border);return true;
    }
    bool BeginGraphic(Point p)
    {
        var kind=Tool switch {"선"=>"line","화살표"=>"arrow","사각형"=>"rectangle","타원"=>"ellipse","형광펜"=>"highlight","밑줄"=>"underline","취소선"=>"strikeout","자유곡선"=>"freehand",_=>null};
        if(kind==null)return false;
        if(kind=="line")return ContinueLine(p);
        ApplyInlineText();Keyboard.Focus(EditCanvas);drawingBefore=Capture();drawingStart=p;drawing=new(){Kind=kind,X=p.X,Y=p.Y,Width=1,Height=1,Points=[new[]{p.X,p.Y}]};SetObjectStyle(drawing);if(kind=="highlight" && ColorValue=="#111111")drawing.Color="#FFEB3B";Page!.Elements.Add(drawing);selectedIds.Clear();selectedId=drawing.Id;EditCanvas.CaptureMouse();CommandManager.InvalidateRequerySuggested();return true;
    }
    bool MoveGraphic(MouseEventArgs e)
    {
        if(continuousLineStart!=null){PreviewContinuousLine(e.GetPosition(EditCanvas));return true;}
        if(drawing==null)return false;if(e.LeftButton!=MouseButtonState.Pressed)return true;
        UpdateGraphic(e.GetPosition(EditCanvas));return true;
    }
    bool HasDrawingGesture(PdfEditElement item)
    {
        if(item.Points.Count<2)return false;
        var zoom=EditSurface.LayoutTransform is ScaleTransform scale?Math.Max(.01,Math.Min(Math.Abs(scale.ScaleX),Math.Abs(scale.ScaleY))):1;
        var minimum=Math.Max(6,Math.Max(SystemParameters.MinimumHorizontalDragDistance,SystemParameters.MinimumVerticalDragDistance))/zoom;
        var points=item.Points;
        var w=points.Max(p=>p[0])-points.Min(p=>p[0]);var h=points.Max(p=>p[1])-points.Min(p=>p[1]);
        return item.Kind is "rectangle" or "ellipse" or "highlight" ? w>=minimum&&h>=minimum : w*w+h*h>=minimum*minimum;
    }
    void DiscardGraphic()
    {
        if(drawing==null)return;
        Page?.Elements.Remove(drawing);drawing=null;
        selectedId=drawingBefore?.Selected??"";selectedIds.Clear();
        if(drawingBefore!=null)selectedIds.UnionWith(drawingBefore.Selection);
        drawingBefore=null;EditCanvas.ReleaseMouseCapture();DrawElements();
    }
    void UpdateGraphic(Point p)
    {
        if(drawing==null)return;var size=SizeOfPage();p.X=Math.Clamp(p.X,0,size.W);p.Y=Math.Clamp(p.Y,0,size.H);
        if(drawing.Kind=="freehand") {if(drawing.Points.Count<10000)drawing.Points.Add(new[]{p.X,p.Y});} else drawing.Points=[new[]{drawingStart.X,drawingStart.Y},new[]{p.X,p.Y}];
        if(drawing.Kind is "underline" or "strikeout") { var y=drawingStart.Y; drawing.Points=[new[]{drawingStart.X,y},new[]{p.X,y}]; }
        if(!HasDrawingGesture(drawing))return;
        // Absolute gesture points remain separate until mouse-up; preview uses a normalized copy.
        var pts=drawing.Points;drawing.X=pts.Min(v=>v[0]);drawing.Y=pts.Min(v=>v[1]);drawing.Width=Math.Max(1,pts.Max(v=>v[0])-drawing.X);drawing.Height=Math.Max(1,pts.Max(v=>v[1])-drawing.Y);
        if(drawing.Kind is "line" or "arrow" or "underline" or "strikeout") {
            var pad=Math.Max(drawing.StrokeWidth, drawing.Kind=="arrow"?drawing.StrokeWidth*4:1);
            var x0=Math.Max(0,drawing.X-pad);var y0=Math.Max(0,drawing.Y-pad);
            drawing.Width=Math.Min(size.W,drawing.X+drawing.Width+pad)-x0;drawing.Height=Math.Min(size.H,drawing.Y+drawing.Height+pad)-y0;drawing.X=x0;drawing.Y=y0;
        }
        drawing.Points=pts.Select(v=>new[]{(v[0]-drawing.X)/drawing.Width,(v[1]-drawing.Y)/drawing.Height}).ToList();DrawElements();drawing.Points=pts;
    }
    void StopDrawingAndSelect()
    {
        if(saving||changingView)return;
        StopContinuousLine();DiscardGraphic();
        DrawingToolsButton.IsChecked=MarkupToolsButton.IsChecked=PenOptions.IsChecked=false;
        EditTool.SelectedIndex=1;DrawElements();Keyboard.Focus(EditCanvas);
    }
    bool EndGraphic()
    {
        if(continuousLineStart!=null){StopContinuousLine();return true;}
        if(drawing==null)return false;
        if(!HasDrawingGesture(drawing)) {DiscardGraphic();return true;}
        drawing.Points=drawing.Points.Select(v=>new[]{Math.Clamp((v[0]-drawing.X)/drawing.Width,0,1),Math.Clamp((v[1]-drawing.Y)/drawing.Height,0,1)}).ToList();
        var before=drawingBefore!;drawing=null;drawingBefore=null;EditCanvas.ReleaseMouseCapture();Commit(before);return true;
    }
    Action? addedTextCommit;
    void CanvasCaptureLost(object sender,MouseEventArgs e){if(FinishAreaSelection(true))return;if(drawing!=null)EndGraphic();FinishObjectDrag();}
    void BeginAddedText()
    {
        if(Selected is not {} item)return;var editBefore=Capture();DrawElements();
        foreach(var visual in EditCanvas.Children.OfType<FrameworkElement>().Where(x=>x is System.Windows.Controls.Primitives.Thumb || x.Tag is PdfEditElement element && element.Id==item.Id).ToArray())EditCanvas.Children.Remove(visual);
        var box=new TextBox {MaxLength=5000,Text=item.Text,Width=item.Width,Height=item.Height,FontFamily=new FontFamily(item.FontFamily),TextAlignment=AlignmentOf(item),Background=item.Kind=="note"?new SolidColorBrush(Color.FromRgb(255,243,175)):Brushes.White,BorderBrush=Brushes.DodgerBlue,BorderThickness=new Thickness(1),Padding=new Thickness(item.Kind=="note"?7:1),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Foreground=(Brush)new BrushConverter().ConvertFromString(item.Color)!,RenderTransformOrigin=new Point(.5,.5),RenderTransform=new RotateTransform(item.Rotation),FontSize=item.FontSize,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap};
        box.IsUndoEnabled=false;box.IsUndoEnabled=true;
        Canvas.SetLeft(box,item.X);Canvas.SetTop(box,item.Y);EditCanvas.Children.Add(box);
        TextBlock.SetLineHeight(box,item.FontSize*1.35);
        var done=false;
        box.TextChanged+=(_,_)=>{if(done)return;FitTextBox(item,box.Text);box.Width=item.Width;box.Height=item.Height;};
        void Finish(){if(done)return;done=true;addedTextCommit=null;var before=editBefore;item.Text=string.IsNullOrWhiteSpace(box.Text)?"텍스트":box.Text;EditText.Text=item.Text;FitTextBox(item,item.Text);Clamp(item);Commit(before);}
        addedTextCommit=Finish;
        box.LostKeyboardFocus+=(_,_)=>Finish();box.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){Finish();e.Handled=true;}};
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,new Action(()=>{if(!done&&EditCanvas.Children.Contains(box)){box.Focus();Keyboard.Focus(box);box.SelectAll();}}));
    }
    void DrawResizeHandle()
    {
        var items=Objects();if(items.Count==0 || !ShowSelectionDecorations)return;
        if(items.Count==1 && items[0].Kind is "line" or "arrow" or "underline" or "strikeout" && items[0].Points.Count==2){DrawEndpointHandles(items[0]);return;}
        var right=items.Max(x=>x.X+x.Width);var bottom=items.Max(x=>x.Y+x.Height);
        var thumb=new System.Windows.Controls.Primitives.Thumb {Width=10,Height=10,Background=Brushes.DodgerBlue,Cursor=Cursors.SizeNWSE};
        Canvas.SetLeft(thumb,right-5);Canvas.SetTop(thumb,bottom-5);EditCanvas.Children.Add(thumb);
        History? before=null;double total=0;
        Action? cancel=null;
        thumb.DragStarted+=(_,_)=>{
            Keyboard.Focus(EditCanvas);before=Capture();total=0;
            cancel=()=>{var snapshot=before;before=null;thumb.CancelDrag();if(snapshot!=null){Restore(snapshot);dirty=!IsClean();}};cancelEditGesture=cancel;CommandManager.InvalidateRequerySuggested();
        };
        thumb.DragDelta+=(_,e)=>{
            if(before==null)return;total+=e.HorizontalChange;
            var original=before.Plan.Pages[before.Page].Elements.Where(x=>items.Any(i=>i.Id==x.Id)).ToArray();
            var left=original.Min(x=>x.X);var top=original.Min(x=>x.Y);var span=Math.Max(1,original.Max(x=>x.X+x.Width)-left);var factor=Math.Clamp(1+total/span,.1,10);
            try{foreach(var x in items){var old=original.Single(i=>i.Id==x.Id);if(items.Count==1 && x.Kind is "text" or "note"){x.AutoWidth=false;x.Width=Math.Max(24,old.Width+total);FitTextBox(x,x.Text);}else{x.Width=Math.Max(1,old.Width*factor);x.Height=Math.Max(1,old.Height*factor);x.X=left+(old.X-left)*factor;x.Y=top+(old.Y-top)*factor;x.FontSize=Math.Clamp(old.FontSize*factor,6,96);Clamp(x);}}
                // Keep the active Thumb alive until DragCompleted.
                foreach(var border in EditCanvas.Children.OfType<Border>().Where(b=>b.Tag is PdfEditElement)){var item=(PdfEditElement)border.Tag;if(!items.Contains(item))continue;border.Width=item.Width;border.Height=item.Height;if(border.Child is Canvas child)child.RenderTransform=new ScaleTransform(item.Width/child.Width,item.Height/child.Height);if(border.Child is TextBlock text)text.FontSize=item.FontSize;Canvas.SetLeft(border,item.X);Canvas.SetTop(border,item.Y);}
                Canvas.SetLeft(thumb,items.Max(x=>x.X+x.Width)-thumb.Width/2);Canvas.SetTop(thumb,items.Max(x=>x.Y+x.Height)-thumb.Height/2);
            }catch(InvalidOperationException){foreach(var x in items){var old=original.Single(i=>i.Id==x.Id);x.X=old.X;x.Y=old.Y;x.Width=old.Width;x.Height=old.Height;x.FontSize=old.FontSize;}}
        };
        thumb.DragCompleted+=(_,e)=>{
            if(ReferenceEquals(cancelEditGesture,cancel))cancelEditGesture=null;
            var snapshot=before;before=null;if(snapshot==null)return;
            if(e.Canceled){Restore(snapshot);dirty=!IsClean();}else if(Math.Abs(total)>.001)Commit(snapshot);else DrawElements();
            CommandManager.InvalidateRequerySuggested();
        };
    }
    void ApplyStyle(object s,RoutedEventArgs e){var before=Capture();foreach(var x in Objects()){SetObjectStyle(x);if(x.Kind is "text" or "note")FitTextBox(x,x.Text);}Commit(before);}
    public void CopyObjects(){copied=PdfEditService.Clone(new(){Pages=[new(){Elements=Objects()}]}).Pages[0].Elements;}
    public void PasteObjects(){if(Page==null||copied.Count==0)return;var before=Capture();selectedIds.Clear();foreach(var x in PdfEditService.Clone(new(){Pages=[new(){Elements=copied}]}).Pages[0].Elements){if(x.Kind=="replace"){x.Kind="text";x.PreserveSourceStyle=false;x.FontFamily="Malgun Gothic";x.SourceTextId=null;x.SourceText=null;}x.Id=Guid.NewGuid().ToString("N");x.X+=12;x.Y+=12;Clamp(x);Page.Elements.Add(x);selectedIds.Add(x.Id);}selectedId=selectedIds.FirstOrDefault()??"";Commit(before);}
    void DuplicateObjects(object s,RoutedEventArgs e){CopyObjects();PasteObjects();}
    void RotateObjects(object s,RoutedEventArgs e){var before=Capture();try{foreach(var x in Objects()){x.Rotation=(x.Rotation+90)%360;Clamp(x);}Commit(before);}catch(Exception ex){Restore(before);EditStatus.Text=ex.Message;}}
    void BringForward(object s,RoutedEventArgs e){var before=Capture();foreach(var x in Objects()){Page!.Elements.Remove(x);Page.Elements.Add(x);}Commit(before);}
    void SendBackward(object s,RoutedEventArgs e){var before=Capture();foreach(var x in Objects().AsEnumerable().Reverse()){Page!.Elements.Remove(x);Page.Elements.Insert(0,x);}Commit(before);}
    static readonly JsonSerializerOptions ProjectJson=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true};
    public async Task SaveProjectAsync(string destination)
    {
        if(saving||info==null)throw new InvalidOperationException("준비 후 저장하세요.");if(File.Exists(destination))throw new IOException("새 파일 이름을 선택하세요.");EndGraphic();ApplyInlineText();saving=true;EnableEditing(false);
        var temp=System.IO.Path.Combine(work,Guid.NewGuid()+".topdf");
        try{var copy=PdfEditService.Clone(plan);await Task.Run(async()=>{using(var zip=ZipFile.Open(temp,ZipArchiveMode.Create)){zip.CreateEntryFromFile(snapshot,"source.pdf");foreach(var item in copy.Pages.SelectMany(x=>x.Elements).Where(x=>x.Kind=="stamp")){var name="assets/"+Guid.NewGuid()+System.IO.Path.GetExtension(item.ImagePath);zip.CreateEntryFromFile(item.ImagePath!,name);item.ImagePath=name;}var entry=zip.CreateEntry("plan.json");await using var stream=entry.Open();await JsonSerializer.SerializeAsync(stream,copy,ProjectJson,lifetime.Token);}},lifetime.Token);await PdfEditService.SaveNewAsync(temp,destination,lifetime.Token);MarkClean();EditStatus.Text="편집 파일 저장 완료";}finally{saving=false;EnableEditing(true);}
    }
    public async Task LoadProjectAsync(string path)
    {
        if(saving)throw new InvalidOperationException("저장 후 열어 주세요.");
        saving=true;EnableEditing(false);try {
        var folder=System.IO.Path.Combine(work,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        using var zip=ZipFile.OpenRead(path);if(zip.GetEntry("plan.json") is not {} jsonEntry || jsonEntry.Length>10_000_000)throw new IOException("편집 계획 한도 초과");if(zip.Entries.Count>5002 || zip.Entries.Sum(x=>x.Length)>300_000_000)throw new IOException("편집 파일 크기 한도 초과");
        await Task.Run(()=>{foreach(var entry in zip.Entries){var full=System.IO.Path.GetFullPath(System.IO.Path.Combine(folder,entry.FullName));if(!full.StartsWith(folder+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||entry.FullName.Contains(':'))throw new IOException("잘못된 편집 파일 경로");Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);entry.ExtractToFile(full,false);}},lifetime.Token);
        var loaded=JsonSerializer.Deserialize<PdfEditPlan>(await File.ReadAllTextAsync(System.IO.Path.Combine(folder,"plan.json")),ProjectJson)??throw new IOException("잘못된 편집 파일");var pdf=System.IO.Path.Combine(folder,"source.pdf");var inspected=await PdfEditService.InspectAsync(pdf,work,lifetime.Token);
        if(loaded.FormatVersion!=1 || loaded.SourceSha256!=inspected.SourceSha256 || loaded.Pages.Count==0 || loaded.Pages.Select(x=>x.SourceIndex).Distinct().Count()!=loaded.Pages.Count || loaded.Pages.Any(x=>x.SourceIndex<0||x.SourceIndex>=inspected.Pages.Length||x.Rotation%90!=0) || loaded.Pages.Sum(x=>x.Elements.Count)>5000)throw new IOException("편집 파일 검증 실패");
        var all=loaded.Pages.SelectMany(x=>x.Elements).ToArray();if(all.Select(x=>x.Id).Distinct().Count()!=all.Length)throw new IOException("중복 편집 항목");
        foreach(var item in all){if(!Guid.TryParseExact(item.Id,"N",out _) || (item.FontFamily is not ("Malgun Gothic" or "Gulim" or "Batang") && !(item.Kind=="replace" && item.PreserveSourceStyle)) || item.Alignment is not ("left" or "center" or "right"))throw new IOException("잘못된 편집 속성");if(!double.IsFinite(item.X+item.Y+item.Width+item.Height+item.FontSize+item.StrokeWidth)||item.Width<=0||item.Height<=0||item.Points.Count>10000||item.Points.Any(p=>p.Length!=2||p.Any(v=>!double.IsFinite(v)||v<0||v>1)))throw new IOException("잘못된 편집 좌표");if(item.Kind=="stamp"){var asset=System.IO.Path.GetFullPath(System.IO.Path.Combine(folder,item.ImagePath??""));if(!asset.StartsWith(folder+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("잘못된 이미지 경로");item.ImagePath=asset;}}
        _ = await PdfEditService.ExportAsync(pdf,folder,loaded,lifetime.Token);
        snapshot=pdf;info=inspected;plan=loaded;undo.Clear();redo.Clear();selectedIds.Clear();selectedId="";selectedSource=null;sourceTexts.Clear();renders.Clear();BindPages(0);MarkClean();await RenderPageAsync();
        } finally {saving=false;EnableEditing(info!=null);}
    }
    async void SaveProject(object s,RoutedEventArgs e){var dialog=new SaveFileDialog{Filter="TOPDF 편집 파일|*.topdf",FileName=System.IO.Path.ChangeExtension(suggestedName,".topdf")};if(dialog.ShowDialog(DialogOwner)!=true)return;try{await SaveProjectAsync(dialog.FileName);}catch(Exception ex){EditStatus.Text=ex.Message;}}
    async void OpenProject(object s,RoutedEventArgs e){if(!CanClose())return;var dialog=new OpenFileDialog{Filter="TOPDF 편집 파일|*.topdf"};if(dialog.ShowDialog(DialogOwner)!=true)return;try{await LoadProjectAsync(dialog.FileName);}catch(Exception ex){EditStatus.Text=ex.Message;}}
}
