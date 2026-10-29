# API 响应格式

所有业务接口（含 health、同步识别、XML 内容识别、后台任务提交、任务查询、验真状态）使用统一响应：

```json
{"code":0,"msg":null,"data":{"type":"电子发票（铁路电子客票）","number":"25000000000000000001"}}
```

data 内的字段保持原有模型；上述仅为精简示例。空字段保留 null。发票本身的 code（发票代码）与外层响应 code 是不同字段，继续保留为 data.code。

成功 code=0，msg=null。任务提交仍返回 HTTP 202，Location 响应头不变，任务编号读取 data.id。任务查询读取 data.state、data.result.invoice；后台任务解析失败时，查询返回 HTTP 422，code=1、msg=原始异常信息、data=null。

错误保留实际 HTTP 状态码，code=1，msg 为异常信息（主动校验失败则为校验说明），data=null。例如：

```json
{"code":1,"msg":"Unsupported file signature.","data":null}
```

鉴权失败、路由不存在、请求绑定错误和服务端异常也采用统一格式；服务端异常不返回内部堆栈。客户端断开连接或服务器在 HTTP 请求进入应用前拒绝请求时，无法保证响应正文。

这是 HTTP 返回格式的兼容性变更：现有客户端需要将原模型访问路径调整到 data 下。独立 Invoice.Core 类库 RecognizeAsync/RecognizeXmlAsync 仍直接返回 InvoiceModel，不受影响。Swagger/OpenAPI 已同步声明 ApiResponse<InvoiceModel>。

