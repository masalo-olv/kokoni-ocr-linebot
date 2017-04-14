#r "Newtonsoft.Json"
#r "System.Configuration"
#r "Microsoft.WindowsAzure.Storage"

using System;
using System.Net;
using System.Net.Http.Headers;
using System.Configuration;
using System.Text;
using System.IO;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.RetryPolicies;

/// <summary>
/// メインメソッド
/// </summary>
/// <param name="req"></param>
/// <param name="log"></param>
/// <returns></returns>
public static async Task<HttpResponseMessage> Run(HttpRequestMessage req, TraceWriter log)
{
    log.Info("Start");

    // リクエストJSONをパース
    string jsonContent = await req.Content.ReadAsStringAsync();
    Request data = JsonConvert.DeserializeObject<Request>(jsonContent);

    string replyToken = null;
    string messageType = null;
    string messageId = null;

    string fileName = DateTime.Now.Year.ToString() + "/" + DateTime.Now.Month.ToString() + "/" + Guid.NewGuid().ToString();
    string containerName = "contents";

    // WebAppsのプロパティ設定からデータを取得
    var SubscriptionKey = ConfigurationManager.AppSettings["SubscriptionKey"];

    // リクエストデータからデータを取得
    foreach (var item in data.events)
    {
        // リプライデータ送付時の認証トークンを取得
        replyToken = item.replyToken.ToString();
        if (item.message != null)
        {
            // メッセージタイプを取得
            messageType = item.message.type.ToString();
            messageId = item.message.id.ToString();
        }
    }

    log.Info(messageId);

    if (messageType != "image") return null;

    Stream responsestream = new MemoryStream();

    // Lineから指定MessageIdの画像を再取得
    responsestream = await GetLineContents(messageId);

    var OCRResponse = new HttpResponseMessage();

    // Conputer vision APIのOCRにリクエスト
    using (var getOCRDataClient = new HttpClient())
    {
        // リクエストパラメータ、とりあえず文字の種類は自動検知
        string language = "unk";
        string detectOrientation = "true";
        var uri = $"https://westus.api.cognitive.microsoft.com/vision/v1.0/ocr?language={language}&detectOrientation={detectOrientation}";

        // ComputerVisionAPIへのリクエスト情報を作成
        HttpRequestMessage OCRRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        OCRRequest.Content = new StreamContent(responsestream);
        OCRRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        // リクエストヘッダーの作成
        getOCRDataClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", $"{SubscriptionKey}");

        // ComputerVisionAPIにリクエスト
        OCRResponse = await getOCRDataClient.SendAsync(OCRRequest);
    }

    // ComputerVisionAPIのレスポンスをパースして文章に
    jsonContent = await OCRResponse.Content.ReadAsStringAsync();
    log.Info(jsonContent);
    OCR_Response ocr_data = JsonConvert.DeserializeObject<OCR_Response>(jsonContent);

    string words= String.Empty;

    if(ocr_data.regions.Any())
    {
        foreach(var regions in ocr_data.regions)
        {
            foreach(var line in regions.lines)
            {
                foreach(var word in line.words)
                {
                    words = words + word.text;
                }
                words = words + Environment.NewLine;
            }
        }
    }
    else
    {
        words = "There is no words!!";
    }

    // リプライデータの作成
    var content = CreateResponse(replyToken, words, log);

    // 日本語に翻訳されたレスポンスをLine ReplyAPIにリクエスト
    await PutLineReply(content);

    // 取得した画像をAzureStorageに保存
    // Lineから指定MessageIdの画像を取得
    responsestream = await GetLineContents(messageId);

    // 取得したファイルをストレージに保管
    await PutLineContentsToStorageAsync(responsestream,containerName, fileName);

    return req.CreateResponse("200");
}

/// <summary>
/// Lineからコンテンツを取得
/// </summary>
/// <returns>Stream</returns>
static async Task<Stream> GetLineContents(string messageId)
{
    Stream responsestream = new MemoryStream();

    // 画像を取得するLine APIを実行
    using (var getContentsClient = new HttpClient())
    {
        //　認証ヘッダーを追加
        getContentsClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {ConfigurationManager.AppSettings["ChannelAccessToken"]}");

        // 非同期でPOST
        var res = await getContentsClient.GetAsync($"https://api.line.me/v2/bot/message/{messageId}/content");
        responsestream = await res.Content.ReadAsStreamAsync();
    }

    return responsestream;
}

/// <summary>
/// Lineににreplyを送信する
/// </summary>
/// <returns>Stream</returns>
static async Task PutLineReply(Response content)
{    
    // JSON形式に変換
    var reqData = JsonConvert.SerializeObject(content);
    
    // レスポンスの作成
    using (var client = new HttpClient())
    {
        // リクエストデータを作成
        // ※HttpClientで[application/json]をHTTPヘッダに追加するときは下記のコーディングじゃないとエラーになる
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://api.line.me/v2/bot/message/reply");
        request.Content = new StringContent(reqData, Encoding.UTF8, "application/json");

        //　認証ヘッダーを追加
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ConfigurationManager.AppSettings["ChannelAccessToken"]}");

        // 非同期でPOST
        var res = await client.SendAsync(request);
    }
}

/// <summary>
/// Lineサーバから取得したStreamを指定ストレージにアップロード
/// </summary>
static async Task PutLineContentsToStorageAsync(Stream stream,string ContainerName,string PathWithFileName)
{
    // ストレージアクセス情報の作成
    var storageAccount = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["AzureStorageAccount"]);
    var blobClient = storageAccount.CreateCloudBlobClient();

    // retry設定 3秒秒3回
    blobClient.DefaultRequestOptions.RetryPolicy = new LinearRetry(TimeSpan.FromSeconds(3), 3);

    var container = blobClient.GetContainerReference(ContainerName);

    await container.CreateIfNotExistsAsync();

    // ストレージアクセスポリシーの設定
    container.SetPermissions(
        new BlobContainerPermissions
        {
            PublicAccess = BlobContainerPublicAccessType.Off,
        });

    // Blob へファイルをアップロード
    var blob = container.GetBlockBlobReference(PathWithFileName);

    await blob.UploadFromStreamAsync(stream);
}

/// <summary>
/// 指定ストレージからコンテンツを取得
/// </summary>
/// <returns>Stream</returns>
static async Task<Stream> GetLineContentsFromStorageAsync(string ContainerName, string PathWithFileName)
{
    // ストレージアクセス情報の作成
    var storageAccount = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["AzureStorageAccount"]);
    var blobClient = storageAccount.CreateCloudBlobClient();

    // retry設定 3秒秒3回
    blobClient.DefaultRequestOptions.RetryPolicy = new LinearRetry(TimeSpan.FromSeconds(3), 3);

    var container = blobClient.GetContainerReference(ContainerName);

    // Blob からダウンロード
    var blob = container.GetBlockBlobReference(PathWithFileName);

    var memoryStream = new MemoryStream();
    
    await blob.DownloadToStreamAsync(memoryStream);
    
    return memoryStream;
}

/// <summary>
/// リプライ情報の作成
/// </summary>
/// <param name="token"></param>
/// <param name="praiseWord"></param>
/// <param name="log"></param>
/// <param name="messageType"></param>
/// <returns></returns>
static Response CreateResponse(string token,string translateWord, TraceWriter log)
{
    Response res = new Response();
    Messages msg = new Messages();

    // リプライトークンはリクエストに含まれるリプライトークンを使う
    res.replyToken = token;
    res.messages = new List<Messages>();

    // メッセージタイプがtext以外は単一のレスポンス情報とする
    msg.type = "text";
    msg.text = translateWord;
    res.messages.Add(msg);

    return res;
}

// ******************************************************
//　リクエスト
public class Request
{
    public List<Event> events { get; set; }
}

//　イベント
public class Event
{
    public string replyToken { get; set; }
    public string type { get; set; }
    public object timestamp { get; set; }
    public Source source { get; set; }
    public message message { get; set; }
}

// ソース
public class Source
{
    public string type { get; set; }
    public string userId { get; set; }
}

// リクエストメッセージ
public class message
{
    public string id { get; set; }
    public string type { get; set; }
    public string text { get; set; }
}
// ******************************************************

// ******************************************************
// レスポンス
public class Response
{
    public string replyToken { get; set; }
    public List<Messages> messages { get; set; }
}

// レスポンスメッセージ
public class Messages
{
    public string type { get; set; }
    public string text { get; set; }
}
// ******************************************************


// ******************************************************
// OCRから返却されたデータ
public class OCR_Response
{
    public string language { get; set; }
    public double textAngle { get; set; }
    public string orientation { get; set; }
    public List<Region> regions { get; set; }
}
public class Region
{
    public string boundingBox { get; set; }
    public List<Line> lines { get; set; }
}
public class Line
{
    public string boundingBox { get; set; }
    public List<Word> words { get; set; }
}
public class Word
{
    public string boundingBox { get; set; }
    public string text { get; set; }
}
// ******************************************************
