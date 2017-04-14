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
    # テスト
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
    var ChannelAccessToken = ConfigurationManager.AppSettings["ChannelAccessToken"];
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
    log.Info(messageType);
    log.Info($"https://api.line.me/v2/bot/message/{messageId}/content");

    if (messageType != "image") return null;

    Stream responsestream = new MemoryStream();

    // 画像を取得するLine APIを実行
    using (var getContentsClient = new HttpClient())
    {
        //　認証ヘッダーを追加
        getContentsClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {ChannelAccessToken}");

        // 非同期でPOST
        var res = await getContentsClient.GetAsync($"https://api.line.me/v2/bot/message/{messageId}/content");
        responsestream = await res.Content.ReadAsStreamAsync();
    }

    // 取得したファイルをストレージに保管
    await PutLineContentsAsync(responsestream,containerName, fileName);

    var OCRStream = await GetLineContentsFromStorageAsync(containerName, fileName);

    var OCRResponse = new HttpResponseMessage();

    // Conputer vision APIのOCRにリクエスト
    using (var getOCRDataClient = new HttpClient())
    {
        // リクエストパラメータ、とりあえず文字の種類は自動検知
        string language = "unk";
        string detectOrientation = "true";
        var uri = $"https://westus.api.cognitive.microsoft.com/vision/v1.0/ocr?language={language}&detectOrientation={detectOrientation}";

        HttpRequestMessage OCRRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        OCRRequest.Content = new StreamContent(responsestream);
        OCRRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        // リクエストヘッダーの作成
        getOCRDataClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", $"{SubscriptionKey}");

        OCRResponse = await getOCRDataClient.SendAsync(OCRStream);

        log.Info(OCRResponse.ToString());
    }



    // ComputerVisionAPIのレスポンスをパースして文章に

    // TranslatorAPIにパースした文字列をリクエストリクエスト

    // 日本語に翻訳されたレスポンスをLine ReplyAPIにリクエスト

    // 取得した画像をAzureStorageに保存

    // リプライデータの作成
    // var content = CreateResponse(replyToken, praiseWord, log, messageType);
    /*
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
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ChannelAccessToken}");

            // 非同期でPOST
            var res = await client.SendAsync(request);

            return req.CreateResponse(res.StatusCode);
        }
        */

    return null;
}

/// <summary>
/// Lineサーバから取得したStreamを指定ストレージにアップロード
/// </summary>
/// <returns>string</returns>
static async Task PutLineContentsToStorageAsync(Stream stream,string ContainerName,string PathWithFileName)
{
    // ストレージアクセス情報の作成
    var storageAccount = CloudStorageAccount.Parse(ConfigurationMsanager.AppSettings["AzureStorageAccount"]);
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
/// <returns>string</returns>
static async Task<Stream> GetLineContentsFromStorageAsync(string ContainerName, string PathWithFileName)
{
    // ストレージアクセス情報の作成
    var storageAccount = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["AzureStorageAccount"]);
    var blobClient = storageAccount.CreateCloudBlobClient();

    // retry設定 3秒秒3回
    blobClient.DefaultRequestOptions.RetryPolicy = new LinearRetry(TimeSpan.FromSeconds(3), 3);

    var container = blobClient.GetContainerReference(ContainerName);

    // ストレージアクセスポリシーの設定
    container.SetPermissions(
        new BlobContainerPermissions
        {
            PublicAccess = BlobContainerPublicAccessType.Off,
        });

    // Blob へファイルをアップロード
    var blob = container.GetBlockBlobReference(PathWithFileName);

    using (var memoryStream = new MemoryStream())
    {
        await blob.DownloadToStreamAsync(memoryStream);
    }

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
static Response CreateResponse(string token, string praiseWord, TraceWriter log, string messageType = "")
{
    Response res = new Response();
    Messages msg = new Messages();

    // リプライトークンはリクエストに含まれるリプライトークンを使う
    res.replyToken = token;
    res.messages = new List<Messages>();

    // メッセージタイプがtext以外は単一のレスポンス情報とする
    if (messageType == "text")
    {
        msg.type = "text";
        msg.text = praiseWord;
        res.messages.Add(msg);

    }
    else
    {
        msg.type = "text";
        msg.text = "画像や動画を見せられても・・・。褒める事が出来るのはあなたの事だけです。";
        res.messages.Add(msg);
    }

    return res;
}


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
