using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using STSApp.Backend.Data;
using STSApp.Backend.Health;
using STSApp.Backend.Hubs;
using STSApp.Backend.Options;
using STSApp.Backend.Repositories;
using STSApp.Backend.Services;
using STSApp.Backend.Services.External;
using STSApp.Backend.Services.Storage;

var builder = WebApplication.CreateBuilder(args);

// enumはC#側では PascalCase、DB/API/SignalRでは snake_case の文字列として扱います。
// 例: TurnStatus.Processing -> "processing"
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

builder.Services
    .AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    });

// 実URLやAPIキーは設計書へ書かず、appsettings / 環境変数から渡します。
builder.Services.Configure<ExternalApiOptions>(builder.Configuration.GetSection(ExternalApiOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

var mysqlConnectionString = builder.Configuration.GetConnectionString("MySql");
if (!string.IsNullOrWhiteSpace(mysqlConnectionString))
{
    // MySQL接続情報が確定したら ConnectionStrings:MySql に設定します。
    // ServerVersionは、まずMySQL 8系を前提に固定し、実DBバージョン確認後に必要なら調整します。
    builder.Services.AddDbContext<StsDbContext>(options =>
        options.UseMySql(mysqlConnectionString, new MySqlServerVersion(new Version(8, 0, 0))));
}

builder.Services.AddScoped<DatabaseHealthCheck>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
builder.Services.AddScoped<IAudioFileStorage, LocalAudioFileStorage>();

var externalApis = builder.Configuration.GetSection(ExternalApiOptions.SectionName).Get<ExternalApiOptions>() ?? new ExternalApiOptions();

if (externalApis.UseDevelopmentMocks)
{
    // 実APIを入れる前に、録音からチャット表示・音声保存までの成功ルートを確認するための開発用実装です。
    // 明示的に UseDevelopmentMocks=true にした時だけ使います。
    builder.Services.AddSingleton<ISttClient, DevelopmentMockSttClient>();
    builder.Services.AddSingleton<IGeminiClient, DevelopmentMockGeminiClient>();
    builder.Services.AddSingleton<ITtsClient, DevelopmentMockTtsClient>();
}
else
{
    if (string.IsNullOrWhiteSpace(externalApis.Stt.BaseUrl))
    {
        // 実URLを設定していない開発段階では、誤って外部APIへ通信しないように未設定用クライアントを使います。
        // Workflow側は同じISttClientだけを見ているため、後で設定を入れても呼び出し側のコードは変わりません。
        builder.Services.AddSingleton<ISttClient, NotConfiguredSttClient>();
    }
    else
    {
        builder.Services.AddHttpClient<ISttClient, HttpSttClient>((serviceProvider, httpClient) =>
        {
            var externalApiOptions = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<ExternalApiOptions>>()
                .Value;

            // STT APIの実URLは appsettings.Development.json や環境変数から渡します。
            // BaseUrl が設定されている場合だけ、このHTTP実装が使われます。
            httpClient.Timeout = TimeSpan.FromSeconds(externalApiOptions.Stt.TimeoutSeconds);
        });
    }

    var geminiOptions = externalApis.Gemini;
    if (string.IsNullOrWhiteSpace(geminiOptions?.ApiKey) || string.IsNullOrWhiteSpace(geminiOptions.ModelName))
    {
        // GeminiはAPIキーとモデル名が両方必要です。
        // 片方だけ設定された状態で中途半端に呼び出さないよう、両方揃うまでは未設定扱いにします。
        builder.Services.AddSingleton<IGeminiClient, NotConfiguredGeminiClient>();
    }
    else
    {
        builder.Services.AddHttpClient<IGeminiClient, HttpGeminiClient>((serviceProvider, httpClient) =>
        {
            var externalApiOptions = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<ExternalApiOptions>>()
                .Value;

            // GeminiのAPIキーとモデル名は設定から渡します。
            // APIキーをAvalonia側へ置かないため、Gemini呼び出しはBackendだけが担当します。
            httpClient.Timeout = TimeSpan.FromSeconds(externalApiOptions.Gemini.TimeoutSeconds);
        });
    }

    if (string.IsNullOrWhiteSpace(externalApis.Tts.BaseUrl))
    {
        // TTSもSTTと同様に、実URLが空なら未設定用クライアントにします。
        // これにより、API情報をまだ入れていない状態でもアプリ全体の流れを確認できます。
        builder.Services.AddSingleton<ITtsClient, NotConfiguredTtsClient>();
    }
    else
    {
        builder.Services.AddHttpClient<ITtsClient, HttpTtsClient>((serviceProvider, httpClient) =>
        {
            var externalApiOptions = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<ExternalApiOptions>>()
                .Value;

            // TTS APIの実URLは appsettings.Development.json や環境変数から渡します。
            // Bearer token等の認証はないため、ヘッダー追加は行いません。
            httpClient.Timeout = TimeSpan.FromSeconds(externalApiOptions.Tts.TimeoutSeconds);
        });
    }
}

builder.Services.AddScoped<IConversationWorkflow, ConversationWorkflow>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.EnsureCreatedAsync(CancellationToken.None);
}

app.MapControllers();
app.MapHub<ConversationHub>("/hubs/conversation");

app.Run();
