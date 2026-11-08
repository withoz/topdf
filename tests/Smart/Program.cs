using System.IO;
using System.Security.Cryptography;
using Topdf;

var root=Path.GetFullPath("artifacts/smart-test");
Environment.SetEnvironmentVariable("TOPDF_DATA_DIR",Path.Combine(root,"service-data"));
var scan=Path.Combine(root,"scan.pdf");
var original=SHA256.HashData(File.ReadAllBytes(scan));
var blank=await DocumentIntelligence.AnalyzeAsync(Path.GetFullPath("artifacts/next-release/synthetic-input/merged-amounts.xls"),Path.Combine(root,"blank.pdf"),default);
Assert(blank.Issues.Any(i=>i.Code=="blank-page") && blank.Issues.Any(i=>i.Code=="amount-suspect"),"Blank and missing amount detection");
var before=await DocumentIntelligence.AnalyzeAsync(scan,scan,default);
Assert(before.Pages[0].NeedsOcr,"Image PDF marked for OCR");
var ocr=await DocumentIntelligence.OcrAsync(scan,default);
var analysis=await DocumentIntelligence.AnalyzeAsync(scan,ocr,default);
var job=new FileJob{Source=scan,Pdf=ocr,Analysis=analysis};
Assert(analysis.Pages[0].Text.Contains("123,456,789") && analysis.Pages[0].Text.Contains("한국어"),"Real Korean/English OCR and amount");
Assert(original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(scan))),"OCR preserves source bytes");
Assert(DocumentIntelligence.Search([job],"123,456,789").Single().Page==1,"Search returns source page");
Assert(DocumentIntelligence.Search([job],"does not exist").Length==0,"No invented search results");
var oldDoc=new FileJob{Source="old.pdf",Analysis=new([new(1,"총금액 100,000원\n같은 문장\n같은 문장",false,false)],[],"")};
var newDoc=new FileJob{Source="new.pdf",Analysis=new([new(1,"총금액 200,000원\n같은 문장",false,false)],[],"")};
var differences=DocumentIntelligence.Compare(oldDoc,newDoc);
Assert(differences.Any(e=>e.Quote.Contains("100,000"))&&differences.Any(e=>e.Quote.Contains("200,000"))&&differences.Any(e=>e.Quote.Contains("같은 문장")),"Amount and duplicate sentence changes");
var evidence=DocumentIntelligence.Excerpts([job],"금액은 얼마인가요?");
Assert(evidence.Length>0,"Grounded excerpt retrieval");
var accepted=LocalDocumentAi.ValidateAnswer("{\"answer\":\"123,456,789 won\",\"citations\":[{\"id\":1,\"quote\":\"Invoice total: 123,456,789 won\"}]}",[new(job,1,"Invoice total: 123,456,789 won")]);
Assert(accepted.Evidence.Length==1,"Exact citation accepted");
try{LocalDocumentAi.ValidateAnswer("{\"answer\":\"invented\",\"citations\":[{\"id\":1,\"quote\":\"invented source quote\"}]}",[new(job,1,"Invoice total: 123,456,789 won")]);throw new Exception("Invented quote accepted");}catch(InvalidDataException){Console.WriteLine("PASS: invented AI citation rejected");}
var watchRoot=Path.Combine(root,"watch-"+Guid.NewGuid().ToString("N"));var input=Path.Combine(watchRoot,"input");var output=Path.Combine(watchRoot,"output");Directory.CreateDirectory(input);Directory.CreateDirectory(output);
try{using var invalid=new FolderAutomation(input,Path.Combine(input,"nested"),_=>Task.FromResult(true),_=>{});throw new Exception("Recursive export path accepted");}catch(ArgumentException){Console.WriteLine("PASS: recursive watch/export path rejected");}
var count=0;var first=new TaskCompletionSource();
using(var watcher=new FolderAutomation(input,output,async path=>{
 var pdf=await new ConversionService().ConvertAsync(path,Path.Combine(watchRoot,"jobs"),default);
 var result=PdfExportService.Export([new(path,pdf)],output);
 if(result.Saved.Count==1){Interlocked.Increment(ref count);first.TrySetResult();return true;}return false;
},message=>Console.WriteLine(message))){
 var source=Path.GetFullPath("artifacts/next-release/synthetic-input/table-and-wrap.docx");
 File.Copy(source,Path.Combine(input,"new.docx"));
 await first.Task.WaitAsync(TimeSpan.FromSeconds(60));
 await Task.Delay(4000);
 Assert(count==1&&Directory.GetFiles(output,"*.pdf").Length==1,"Real folder conversion, export, event deduplication");
}
if(args.Contains("--real-ai")){
 try{
  var names=await LocalDocumentAi.ModelsAsync(default);Assert(names.Contains(LocalAiHost.ModelName),"Bundled offline model available");
  var answer=await LocalDocumentAi.AskAsync(LocalAiHost.ModelName,"청구 금액은 얼마인가요?",[new(job,1,"Invoice total: 123,456,789 won")],default);
  Assert(answer.Answer.Contains("123,456,789")&&answer.Evidence.Length>0,"Real local model answer and verified quote");
  Console.WriteLine(answer.Answer);
 }finally{LocalAiHost.Stop();}
}
Console.WriteLine("PASS: smart document checks completed");
static void Assert(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS: "+name);}
