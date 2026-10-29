# Mitzh.Invoice.Api

[![Build](https://github.com/Mitzh/Invoice.Api/actions/workflows/ci.yml/badge.svg)](https://github.com/Mitzh/Invoice.Api/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Mitzh.Invoice.Api.svg)](https://www.nuget.org/packages/Mitzh.Invoice.Api)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![Powered by Codex](https://img.shields.io/badge/Powered%20by-Codex-412991)](https://openai.com/codex/)

> 本项目由 **Codex 驱动支持**，使用 Codex 辅助完成开发、重构、测试和文档维护。

代码命名空间：`Mitzh.Invoice.Api`。本项目拥有独立目录、解决方案、Git 仓库和发布流程。

基于 ASP.NET Core 10 的离线发票识别服务，提供 Swagger、同步文件/流、XML 内容及可选后台任务接口。以 **.NET 工具包**发布，命令名 `mitzh-invoice-api`，不是可引用的类库包；在 C# 中嵌入解析请安装 `Mitzh.Invoice.Core`。

## 安装与启动

本机需有 .NET 10 SDK（工具安装）和 ASP.NET Core 10 Runtime（服务运行）。

```powershell
dotnet tool install --global Mitzh.Invoice.Api --version 3.0.0
$env:Limits__ApiKey = 'replace-with-your-own-key'
mitzh-invoice-api --urls http://127.0.0.1:5088
```

默认 Production 要求设置 API key。可用 `--Limits:ApiKey=...`、环境变量或 .NET 常规配置设置参数，建议环境变量。Swagger 地址：<http://127.0.0.1:5088/swagger/index.html>，Authorize 填入 key，不添加 Bearer 前缀。

工具包含配套中文 OCR 模型；扫描 PDF 另外安装本机 `pdftoppm` 并加入 PATH，或设置 `$env:Limits__PdfToPpm` 为完整路径。原生文字 PDF/XML/OFD 无需 PDF 图片渲染程序。模型可由 `Limits__DetModel`、`Limits__ClsModel`、`Limits__RecModel`、`Limits__Dictionary` 覆盖。

## 上传文件

```powershell
curl.exe -H "X-Api-Key: replace-with-your-own-key" -F "file=@D:\invoices\sample.pdf" http://127.0.0.1:5088/api/invoices/recognize
```

文件流（不要求 multipart 或文件名）：

```powershell
curl.exe -H "X-Api-Key: replace-with-your-own-key" -H "Content-Type: application/octet-stream" --data-binary "@D:\invoices\sample.ofd" http://127.0.0.1:5088/api/invoices/recognize
```

XML 内容：

```powershell
$headers = @{ 'X-Api-Key' = 'replace-with-your-own-key' }
$body = @{ xml = Get-Content -Raw 'D:\invoices\sample.xml' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5088/api/invoices/recognize/xml' -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $body
```

C# HTTP 客户端：

```csharp
using System.Net.Http.Json;
using Mitzh.Invoice.Core;

using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5088") };
client.DefaultRequestHeaders.Add("X-Api-Key", "replace-with-your-own-key");
await using var input = File.OpenRead(@"D:\invoices\sample.pdf");
using var content = new StreamContent(input);
using var response = await client.PostAsync("/api/invoices/recognize", content);
var result = await response.Content.ReadFromJsonAsync<InvoiceResponse<InvoiceModel>>();
if (result?.Code != 0)
{
    throw new InvalidOperationException(result?.Msg ?? "API request failed.");
}
InvoiceModel invoice = result.Data;

public sealed record InvoiceResponse<T>(int Code, string Msg, T Data);
```

客户端引用 `Mitzh.Invoice.Core` 仅用于 InvoiceModel 类型；远程服务执行识别，客户端不必加载模型。

## 响应与接口

成功为 `{"code":0,"msg":null,"data":...}`；失败为 `{"code":1,"msg":"异常信息或校验说明","data":null}`，同时保留 HTTP 400/401/404/413/415/422/429/500/503/504。解析字段缺失可能返回成功并带 warnings，仍需复核。

| 方法 | 路由 | 作用 |
| --- | --- | --- |
| GET | `/health` | 服务及 OCR 状态，需 API key |
| POST | `/api/invoices/recognize` | multipart 或原始流，data 为 InvoiceModel |
| POST | `/api/invoices/recognize/xml` | JSON xml 内容，data 为 InvoiceModel |
| POST | `/api/invoices` | multipart 后台任务，202，data.id 及 Location |
| GET | `/api/jobs/{id}` | data.state / data.result；解析失败返回 422、code=1 |
| GET | `/api/jobs/{id}/verification` | 当前为 NotConfigured |

进程内后台任务和疑似查重不持久化；默认 20 MiB/30 页/120 秒，4 个并发上传。临时数据默认写入操作系统临时目录 `Mitzh.Invoice.Api`，可设置 `Limits__TempDirectory`。远程部署请配置 AllowedHosts、HTTPS 反向代理与访问控制；默认绑定本机。

**解析不代表数字签名验证或税务验真。** 模型、第三方库及外部 Poppler 各自遵守其许可证。完整源码、格式规范及调用说明：[Mitzh/Invoice.Api](https://github.com/Mitzh/Invoice.Api)。

## 独立源码构建

通过 NuGet 引用 [Mitzh.Invoice.Core 3.0.0](https://www.nuget.org/packages/Mitzh.Invoice.Core/3.0.0)，不包含 Core 源码，也不依赖相邻目录。API 自身命名空间为 `Mitzh.Invoice.Api`，调用发票模型时引入 `Mitzh.Invoice.Core`。

```powershell
dotnet restore Invoice.Api.sln --locked-mode
dotnet build Invoice.Api.sln -c Release --no-restore
./Run.ps1
# 另开终端运行 49 项独立 HTTP 检查（自动启动测试服务）
./tests/RunChecks.ps1
dotnet pack src/Invoice.Api -c Release -o artifacts
```

## 版本迁移

2.0.0 将原 `using Mitzh;` 拆分为各项目专属命名空间。类库调用者改用 `using Mitzh.Invoice.Core;`；API 类型位于 `Mitzh.Invoice.Api`。现有 HTTP 路由和 JSON 字段保持兼容。1.0.0 已发布版本不会被覆盖。

## .NET 10 升级（3.0.0）

3.0.0 仅面向 `net10.0`，需要 .NET 10 SDK / Runtime；Api 需要 ASP.NET Core 10 Runtime。原有命名空间、调用签名、HTTP 路由和 JSON 返回格式保持不变。Api 通过 NuGet 引用 Core 3.0.0，两个仓库继续独立构建。`global.json` 限定使用 .NET 10 SDK。需要继续使用 .NET 8 的应用可保留 2.x，不应直接升级到 3.x。
