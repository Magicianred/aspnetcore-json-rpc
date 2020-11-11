﻿// © Alexander Kozlenko. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Anemonis.AspNetCore.JsonRpc.Resources;
using Anemonis.JsonRpc;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using Newtonsoft.Json;

namespace Anemonis.AspNetCore.JsonRpc
{
    /// <summary>Represents a middleware for adding a JSON-RPC handler to the application's request pipeline.</summary>
    /// <typeparam name="T">The type of the handler.</typeparam>
    public sealed class JsonRpcMiddleware<T> : JsonRpcMiddleware, IMiddleware, IDisposable
        where T : class, IJsonRpcHandler
    {
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger _logger;
        private readonly T _handler;
        private readonly JsonRpcSerializer _serializer;

        /// <summary>Initializes a new instance of the <see cref="JsonRpcMiddleware{T}" /> class.</summary>
        /// <param name="services">The <see cref="IServiceProvider" /> instance for retrieving service objects.</param>
        /// <param name="environment">The <see cref="IWebHostEnvironment" /> instance for retrieving web hosting environment information.</param>
        /// <param name="logger">The <see cref="ILogger{T}" /> instance for logging.</param>
        /// <exception cref="ArgumentNullException"><paramref name="services" />, <paramref name="environment" />, or <paramref name="logger" /> is <see langword="null" />.</exception>
        public JsonRpcMiddleware(IServiceProvider services, IWebHostEnvironment environment, ILogger<JsonRpcMiddleware<T>> logger)
        {
            if (services is null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            if (environment is null)
            {
                throw new ArgumentNullException(nameof(environment));
            }
            if (logger is null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _environment = environment;
            _logger = logger;
            _handler = services.GetService<T>() ?? ActivatorUtilities.CreateInstance<T>(services);
            _serializer = new(CreateJsonRpcContractResolver(_handler), services.GetService<IOptions<JsonRpcOptions>>()?.Value?.JsonSerializer);
        }

        private static bool IsReservedErrorCode(long code)
        {
            return
                (code == JsonRpcErrorCode.InvalidFormat) ||
                (code == JsonRpcErrorCode.InvalidMethod) ||
                (code == JsonRpcErrorCode.InvalidMessage) ||
                (code == JsonRpcHandlerErrorCode.BatchHasDuplicateIdentifiers);
        }

        private static JsonRpcContractResolver CreateJsonRpcContractResolver(T handler)
        {
            var contracts = handler.GetContracts();
            var resolver = new JsonRpcContractResolver();

            foreach (var kvp in contracts)
            {
                if (kvp.Key is null)
                {
                    throw new InvalidOperationException(Strings.GetString("handler.contract.method_name.invalid_value"));
                }
                if (JsonRpcProtocol.IsSystemMethod(kvp.Key))
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.GetString("handler.contract.method_name.reserved_prefix"), kvp.Key));
                }

                resolver.AddRequestContract(kvp.Key, kvp.Value);
            }

            return resolver;
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentNullException"><paramref name="context" /> is <see langword="null" />.</exception>
        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!string.Equals(context.Request.Method, HttpMethods.Post, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;

                return;
            }
            if (context.Request.Headers.ContainsKey(HeaderNames.ContentEncoding))
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;

                return;
            }
            if (!context.Request.Headers.TryGetValue(HeaderNames.ContentType, out var contentTypeHeaderValueString))
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;

                return;
            }
            if (!MediaTypeHeaderValue.TryParse((string)contentTypeHeaderValueString, out var contentTypeHeaderValue))
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;

                return;
            }
            if (!contentTypeHeaderValue.MediaType.Equals(JsonRpcTransport.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;

                return;
            }

            var requestStreamEncoding = default(Encoding);

            if (contentTypeHeaderValue.Charset.HasValue && !JsonRpcTransport.CharsetEncodings.TryGetValue(contentTypeHeaderValue.Charset.Value, out requestStreamEncoding))
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;

                return;
            }

            requestStreamEncoding ??= JsonRpcTransport.CharsetEncodings[Encoding.UTF8.WebName];

            var responseStreamEncoding = JsonRpcTransport.CharsetEncodings[Encoding.UTF8.WebName];
            var jsonRpcRequestData = default(JsonRpcData<JsonRpcRequest>);

            try
            {
                jsonRpcRequestData = await _serializer.DeserializeRequestDataAsync(context.Request.Body, requestStreamEncoding, context.RequestAborted);
            }
            catch (JsonException e)
            {
                _logger.LogDataIsInvalid(e);

                var jsonRpcResponse = StandardJsonRpcResponses[JsonRpcErrorCode.InvalidFormat];

                if (_environment.EnvironmentName == Environments.Development)
                {
                    jsonRpcResponse = new(default, new JsonRpcError(jsonRpcResponse.Error.Code, jsonRpcResponse.Error.Message, e.ToString()));
                }

                context.Response.StatusCode = StatusCodes.Status200OK;

                await SerializeJsonRpcResponseAsync(context, jsonRpcResponse, responseStreamEncoding);

                return;
            }
            catch (JsonRpcSerializationException e)
            {
                _logger.LogDataIsInvalid(e);

                var jsonRpcResponse = StandardJsonRpcResponses[JsonRpcErrorCode.InvalidOperation];

                if (_environment.EnvironmentName == Environments.Development)
                {
                    jsonRpcResponse = new(default, new JsonRpcError(jsonRpcResponse.Error.Code, jsonRpcResponse.Error.Message, e.ToString()));
                }

                context.Response.StatusCode = StatusCodes.Status200OK;

                await SerializeJsonRpcResponseAsync(context, jsonRpcResponse, responseStreamEncoding);

                return;
            }

            if (!jsonRpcRequestData.IsBatch)
            {
                var jsonRpcRequestItem = jsonRpcRequestData.Item;

                context.RequestAborted.ThrowIfCancellationRequested();

                var jsonRpcResponse = await CreateJsonRpcResponseAsync(jsonRpcRequestItem, 0);

                if (jsonRpcResponse is null)
                {
                    context.Response.StatusCode = StatusCodes.Status204NoContent;

                    return;
                }
                if (jsonRpcRequestItem.IsValid && jsonRpcRequestItem.Message.IsNotification)
                {
                    context.Response.StatusCode = StatusCodes.Status204NoContent;

                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;

                await SerializeJsonRpcResponseAsync(context, jsonRpcResponse, responseStreamEncoding);
            }
            else
            {
                var jsonRpcRequestItems = jsonRpcRequestData.Items;
                var jsonRpcRequestIdentifiers = new HashSet<JsonRpcId>();

                for (var i = 0; i < jsonRpcRequestItems.Count; i++)
                {
                    var jsonRpcRequestItem = jsonRpcRequestItems[i];

                    if (jsonRpcRequestItem.IsValid && !jsonRpcRequestItem.Message.IsNotification)
                    {
                        if (!jsonRpcRequestIdentifiers.Add(jsonRpcRequestItem.Message.Id))
                        {
                            _logger.LogBatchHasDuplicateIdentifiers();

                            var jsonRpcResponse = StandardJsonRpcResponses[JsonRpcHandlerErrorCode.BatchHasDuplicateIdentifiers];

                            context.Response.StatusCode = StatusCodes.Status200OK;

                            await SerializeJsonRpcResponseAsync(context, jsonRpcResponse, responseStreamEncoding);

                            return;
                        }
                    }
                }

                var jsonRpcResponses = new List<JsonRpcResponse>(jsonRpcRequestItems.Count);

                for (var i = 0; i < jsonRpcRequestItems.Count; i++)
                {
                    context.RequestAborted.ThrowIfCancellationRequested();

                    var jsonRpcRequestItem = jsonRpcRequestItems[i];
                    var jsonRpcResponse = await CreateJsonRpcResponseAsync(jsonRpcRequestItem, i);

                    if (jsonRpcResponse is not null)
                    {
                        if (jsonRpcRequestItem.IsValid && jsonRpcRequestItem.Message.IsNotification)
                        {
                            continue;
                        }

                        jsonRpcResponses.Add(jsonRpcResponse);
                    }
                }

                if (jsonRpcResponses.Count == 0)
                {
                    // Server must return empty content for empty response batch according to the JSON-RPC 2.0 specification

                    context.Response.StatusCode = StatusCodes.Status204NoContent;

                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;

                await SerializeJsonRpcResponsesAsync(context, jsonRpcResponses, responseStreamEncoding);
            }
        }

        private async Task SerializeJsonRpcResponseAsync(HttpContext context, JsonRpcResponse jsonRpcResponse, Encoding encoding)
        {
            context.Response.ContentType = ContentTypeHeaderValue;

            using (var responseStream = new MemoryStream())
            {
                await _serializer.SerializeResponseAsync(jsonRpcResponse, responseStream, encoding, context.RequestAborted);

                context.Response.ContentLength = responseStream.Length;
                responseStream.Position = 0L;

                await responseStream.CopyToAsync(context.Response.Body);
            }
        }

        private async Task SerializeJsonRpcResponsesAsync(HttpContext context, IReadOnlyList<JsonRpcResponse> jsonRpcResponses, Encoding encoding)
        {
            context.Response.ContentType = ContentTypeHeaderValue;

            using (var responseStream = new MemoryStream())
            {
                await _serializer.SerializeResponsesAsync(jsonRpcResponses, responseStream, encoding, context.RequestAborted);

                context.Response.ContentLength = responseStream.Length;
                responseStream.Position = 0L;

                await responseStream.CopyToAsync(context.Response.Body);
            }
        }

        private async Task<JsonRpcResponse> CreateJsonRpcResponseAsync(JsonRpcMessageInfo<JsonRpcRequest> requestInfo, int index)
        {
            if (!requestInfo.IsValid)
            {
                var exception = requestInfo.Exception;

                _logger.LogRequestIsInvalid(index, exception);

                var jsonRpcError = StandardJsonRpcErrors[exception.ErrorCode];

                if (_environment.EnvironmentName == Environments.Development)
                {
                    jsonRpcError = new(jsonRpcError.Code, jsonRpcError.Message, exception.ToString());
                }

                return new(exception.MessageId, jsonRpcError);
            }

            var request = requestInfo.Message;
            var requestId = request.Id;
            var response = await _handler.HandleAsync(request);

            if (response is not null)
            {
                var responseId = response.Id;

                if (requestId != responseId)
                {
                    throw new InvalidOperationException(Strings.GetString("handler.invalid_response_id"));
                }

                if (!response.Success)
                {
                    if (IsReservedErrorCode(response.Error.Code))
                    {
                        throw new InvalidOperationException(Strings.GetString("handler.invalid_error_code"));
                    }
                }

                if (!request.IsNotification)
                {
                    if (response.Success)
                    {
                        _logger.LogHandledRequestWithResultSuccessfully(index);
                    }
                    else
                    {
                        _logger.LogHandledRequestWithErrorSuccessfully(response.Error.Code, index);
                    }
                }
                else
                {
                    if (response.Success)
                    {
                        _logger.LogHandledRequestWithResultAsNotification(index);
                    }
                    else
                    {
                        _logger.LogHandledRequestWithErrorAsNotification(response.Error.Code, index);
                    }
                }
            }
            else
            {
                if (request.IsNotification)
                {
                    _logger.LogHandledNotificationSuccessfully(index);
                }
                else
                {
                    _logger.LogHandledNotificationAsRequest(index);
                }
            }

            return response;
        }

        /// <summary>Disposes the corresponding instance of a JSON-RPC handler.</summary>
        public void Dispose()
        {
            (_handler as IDisposable)?.Dispose();
        }
    }
}
