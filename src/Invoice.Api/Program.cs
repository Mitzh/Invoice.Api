using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Mitzh.Invoice.Core;
using Mitzh.Invoice.Api;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi.Models;

WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
InvoiceRecognitionOptions limits = builder.Configuration.GetSection("Limits").Get<InvoiceRecognitionOptions>() ?? new();
string modelDirectory = Path.Combine(AppContext.BaseDirectory, "models");
if (string.IsNullOrWhiteSpace(limits.DetModel))
{
    limits.DetModel = Path.Combine(modelDirectory, "ch_PP-OCRv5_mobile_det.onnx");
}
if (string.IsNullOrWhiteSpace(limits.ClsModel))
{
    limits.ClsModel = Path.Combine(modelDirectory, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
}
if (string.IsNullOrWhiteSpace(limits.RecModel))
{
    limits.RecModel = Path.Combine(modelDirectory, "ch_PP-OCRv5_rec_mobile.onnx");
}
if (string.IsNullOrWhiteSpace(limits.Dictionary))
{
    limits.Dictionary = Path.Combine(modelDirectory, "ppocrv5_dict.txt");
}
if (builder.Configuration["Limits:TempDirectory"] == null)
{
    limits.TempDirectory = Path.Combine(Path.GetTempPath(), "Mitzh.Invoice.Api", "recognition");
}

if (limits.Workers is < 1 or > 8 || limits.QueueCapacity is < 1 or > 1000 || limits.MaxJobs < 1 || limits.MaxBytes < 1 || limits.MaxPages is < 1 or > 100 || limits.TimeoutSeconds < 1)
{
    throw new InvalidOperationException("Invalid Limits configuration.");
}

if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(limits.ApiKey))
{
    throw new InvalidOperationException("Configure Limits__ApiKey outside Development.");
}

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = limits.MaxBytes + 1024 * 1024);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = limits.MaxBytes + 1024 * 1024;
    o.MemoryBufferThreshold = 64 * 1024;
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(limits);
builder.Services.AddSingleton(_ => new InvoiceRecognizer(limits));
builder.Services.AddSingleton<IInvoiceVerifier, UnconfiguredVerifier>();
builder.Services.AddSingleton<JobService>();
builder.Services.AddHostedService(s => s.GetRequiredService<JobService>());
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "电子发票识别 API", Version = "v1", Description = "点击 Authorize 填入 X-Api-Key，再上传 XML/OFD/PDF/JPG/PNG，或使用 XML 内容接口。成功返回 code=0、msg=null、data=InvoiceModel；失败 code=1、msg=异常信息、data=null；Warnings 表示待复核问题。税务验真及 XML 验签暂未配置。" });
    o.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Api-Key", Description = "填写服务启动时设置的 Limits__ApiKey，无需添加 Bearer 前缀。" });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }] = Array.Empty<string>() });
    o.OperationFilter<InvoiceUploadOperationFilter>();
    o.SchemaFilter<ApiResponseSchemaFilter>();
});
builder.Services.AddRateLimiter(o => o.AddPolicy("uploads", _ => System.Threading.RateLimiting.RateLimitPartition.GetConcurrencyLimiter("uploads", _ => new() { PermitLimit = 4, QueueLimit = 0 })));
WebApplication app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = 500;
    await context.Response.WriteAsJsonAsync(new ApiResponse<object>(1, context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error.Message ?? "Internal server error.", null));
}));
app.UseStatusCodePages(async status =>
{
    HttpResponse response = status.HttpContext.Response;
    await response.WriteAsJsonAsync(new ApiResponse<object>(1, Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(response.StatusCode), null));
});
app.UseRateLimiter();
if (builder.Configuration.GetValue("Swagger:Enabled", true))
{
    // Serve local Swagger assets before API-key middleware so the Authorize dialog can be opened.
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.DocumentTitle = "电子发票识别 API - 手动验证";
        o.SwaggerEndpoint("v1/swagger.json", "电子发票识别 API v1");
        o.DisplayRequestDuration();
    });
}

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    if (limits.ApiKey is { Length: > 0 } key && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Api-Key"].ToString())), SHA256.HashData(Encoding.UTF8.GetBytes(key))))
    {
        ctx.Response.StatusCode = 401;
        return;
    }

    await next(ctx);
});
app.MapGet("/health", (InvoiceRecognizer p) => ApiResult.Ok(new { status = "ok", ocrConfigured = p.OcrConfigured, verification = "NotConfigured", storage = "ProcessMemory", version = "2.0.0" })).WithTags("服务状态").WithSummary("检查服务与 OCR 配置");
app.MapGet("/api/jobs/{id:guid}", (Guid id, JobService jobs) => jobs.Get(id) is { } job ? job.State == "Failed" ? ApiResult.Error(job.Error, 422) : ApiResult.Ok(job) : ApiResult.NotFound()).WithTags("后台任务（可选）").WithSummary("查询异步任务及解析结果").Produces<ApiResponse<JobView>>().Produces<ApiResponse<object>>(404).Produces<ApiResponse<object>>(422);
app.MapGet("/api/jobs/{id:guid}/verification", (Guid id, JobService jobs) => jobs.Get(id) != null ? ApiResult.Ok(new { state = "NotConfigured", message = "离线部署未接入授权验真接口；解析及金额检查不代表税务验真。" }) : ApiResult.NotFound()).WithTags("后台任务（可选）").WithSummary("查询验真配置状态（当前未接入）");
// Simple call: upload a file, receive InvoiceModel directly. Background jobs remain optional.
app.MapPost("/api/invoices/recognize", async (HttpRequest request, InvoiceRecognizer recognizer, CancellationToken ct) =>
{
    try
    {
        if (!request.HasFormContentType)
        {
            if (request.ContentLength > limits.MaxBytes)
            {
                return ApiResult.Error("File exceeds size limit.", statusCode: 413);
            }

            return ApiResult.Ok(await recognizer.RecognizeAsync(request.Body, ct));
        }

        IFormCollection form = await request.ReadFormAsync(ct);
        if (form.Files.Count != 1)
        {
            return ApiResult.Error("Exactly one file is required.", statusCode: 400);
        }

        if (form.Files[0].Length == 0 || form.Files[0].Length > limits.MaxBytes)
        {
            return ApiResult.Error("Invalid file size.", statusCode: 413);
        }

        await using Stream stream = form.Files[0].OpenReadStream();
        return ApiResult.Ok(await recognizer.RecognizeAsync(stream, ct));
    }
    catch (InvalidDataException ex)
    {
        return ApiResult.Error(ex.Message, statusCode: 422);
    }
    catch (System.Xml.XmlException ex)
    {
        return ApiResult.Error(ex.Message, statusCode: 422);
    }
    catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
    {
        return ApiResult.Error(ex.Message, statusCode: 504);
    }
    catch (InvalidOperationException ex) when (ex.Message.StartsWith("OCR_NOT_CONFIGURED"))
    {
        return ApiResult.Error(ex.Message, statusCode: 503);
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
        return ApiResult.Error(ex.Message, statusCode: 503);
    }
}).DisableAntiforgery().RequireRateLimiting("uploads").WithTags("发票识别").WithSummary("上传文件，返回统一响应及发票模型").WithDescription("推荐入口。上传 XML/OFD/PDF/JPG/PNG 文件；也支持原始文件流及 application/xml 原文。XML 为结构化直读，不依赖 OCR。").Produces<ApiResponse<InvoiceModel>>().Produces<ApiResponse<object>>(400).Produces<ApiResponse<object>>(413).Produces<ApiResponse<object>>(422).Produces<ApiResponse<object>>(503).Produces<ApiResponse<object>>(504).Produces<ApiResponse<object>>(401).Produces<ApiResponse<object>>(429);
app.MapPost("/api/invoices/recognize/xml", async (XmlInvoiceRequest payload, InvoiceRecognizer recognizer, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(payload.Xml))
    {
        return ApiResult.Error("xml must contain an EInvoice XML document.", statusCode: 400);
    }

    if (Encoding.UTF8.GetByteCount(payload.Xml) > limits.MaxBytes)
    {
        return ApiResult.Error("XML content exceeds size limit.", statusCode: 413);
    }

    try
    {
        return ApiResult.Ok(await recognizer.RecognizeXmlAsync(payload.Xml, ct));
    }
    catch (System.Xml.XmlException ex)
    {
        return ApiResult.Error(ex.Message, statusCode: 422);
    }
    catch (InvalidDataException ex)
    {
        return ApiResult.Error(ex.Message, statusCode: 422);
    }
    catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
    {
        return ApiResult.Error(ex.Message, statusCode: 504);
    }
}).RequireRateLimiting("uploads").WithTags("发票识别").WithSummary("解析 XML 数电票内容，返回统一响应及发票模型").WithDescription("将完整 XML 原文放入 JSON 的 xml 字段。支持 EInvoice 通用票头、购销方、金额和重复明细；不验证 XML 数字签名。示例为合成数据。").Produces<ApiResponse<InvoiceModel>>().Produces<ApiResponse<object>>(400).Produces<ApiResponse<object>>(413).Produces<ApiResponse<object>>(422).Produces<ApiResponse<object>>(504).Produces<ApiResponse<object>>(401).Produces<ApiResponse<object>>(429);
app.MapPost("/api/invoices", async (HttpRequest request, JobService jobs, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
    {
        return ApiResult.Error("Use multipart/form-data with one file field.", statusCode: 415);
    }

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync(ct);
    }
    catch (InvalidDataException ex)
    {
        return ApiResult.Error(ex.Message, statusCode: 413);
    }

    if (form.Files.Count != 1)
    {
        return ApiResult.Error("Exactly one file is required; submit each invoice as a separate job.", statusCode: 400);
    }

    IFormFile file = form.Files[0];
    string ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".xml" or ".ofd" or ".pdf" or ".png" or ".jpg" or ".jpeg"))
    {
        return ApiResult.Error("Supported: XML, OFD, PDF, PNG, JPG.", statusCode: 415);
    }

    if (file.Length == 0 || file.Length > limits.MaxBytes)
    {
        return ApiResult.Error("File size exceeds limit or is empty.", statusCode: 413);
    }

    string dir = Path.Combine(limits.TempDirectory, "pending", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "input" + ext);
    bool accepted = false;
    try
    {
        await using (FileStream target = File.Create(path))
        {
            await file.CopyToAsync(target, ct);
        }

        byte[] signature = new byte[512];
        int read;
        await using (FileStream input = File.OpenRead(path))
        {
            read = await input.ReadAtLeastAsync(signature, signature.Length, false, ct);
        }

        bool valid;
        try
        {
            valid = InvoiceFileFormat.Detect(signature.AsSpan(0, read)) == (ext == ".jpeg" ? ".jpg" : ext);
        }
        catch (InvalidDataException)
        {
            valid = false;
        }

        if (!valid)
        {
            return ApiResult.Error("File extension and signature do not match.", statusCode: 415);
        }

        Guid? id = jobs.Submit(path, ext);
        if (id == null)
        {
            return ApiResult.Error("Queue or in-memory job limit reached. Restart clears retained jobs.", statusCode: 429);
        }

        accepted = true;
        return ApiResult.Accepted($"/api/jobs/{id}", new
        {
            id,
            state = "Queued",
            verification = "NotConfigured"
        });
    }
    finally
    {
        if (!accepted)
        {
            Directory.Delete(dir, true);
        }
    }
}).DisableAntiforgery().RequireRateLimiting("uploads").WithTags("后台任务（可选）").WithSummary("上传文件并创建异步解析任务").WithDescription("返回 202 和任务 id，再调用 GET /api/jobs/{id} 查询结果。").Produces(202).Produces<ApiResponse<object>>(400).Produces<ApiResponse<object>>(413).Produces<ApiResponse<object>>(415).Produces<ApiResponse<object>>(401).Produces<ApiResponse<object>>(429);
app.Run();
