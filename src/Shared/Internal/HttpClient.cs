﻿/*----------------------------------------------------------------------------------
// Copyright 2019 Huawei Technologies Co.,Ltd.
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License.  You may obtain a copy of the
// License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations under the License.
//----------------------------------------------------------------------------------*/
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using OBS.Internal.Log;
using OBS.Internal.Auth;
using System.Reflection;
using OBS.Model;

namespace OBS.Internal
{
    internal partial class HttpClient
    {
        internal HttpClient(ObsConfig obsConfig)
        {
            ServicePointManager.DefaultConnectionLimit = obsConfig.ConnectionLimit;
            ServicePointManager.MaxServicePointIdleTime = obsConfig.MaxIdleTime;
            ServicePointManager.Expect100Continue = false;

            if (obsConfig.SecurityProtocolType.HasValue)
            {
                ServicePointManager.SecurityProtocol = obsConfig.SecurityProtocolType.Value;
            }
        }


        internal Signer GetSigner(HttpContext context)
        {
            switch (context.ChooseAuthType)
            {
                case AuthTypeEnum.V2:
                    return V2Signer.GetInstance();
                case AuthTypeEnum.V4:
                    return V4Signer.GetInstance();
                default:
                    return ObsSigner.GetInstance();
            }
        }

        internal IHeaders GetIHeaders(HttpContext context)
        {
            switch (context.ChooseAuthType)
            {
                case AuthTypeEnum.V2:
                case AuthTypeEnum.V4:
                    return V2Headers.GetInstance();
                default:
                    return ObsHeaders.GetInstance();
            }
        }

        internal HttpResponse DoRequest(HttpRequest httpRequest, HttpContext context)
        {
            if (!context.SkipAuth)
            {
                GetSigner(context).DoAuth(httpRequest, context, GetIHeaders(context));
            }

            if (!context.ObsConfig.KeepAlive && !httpRequest.Headers.ContainsKey(Constants.CommonHeaders.Connection))
            {
                httpRequest.Headers.Add(Constants.CommonHeaders.Connection, "Close");
            }

            var request = HttpWebRequestFactory.BuildWebRequest(httpRequest, context);

            var reqTime = DateTime.Now;

            if (httpRequest.Method is HttpVerb.PUT or HttpVerb.POST or HttpVerb.DELETE)
            {
                SetContent(request, httpRequest, context.ObsConfig);
            }

            try
            {
                return new HttpResponse(request.GetResponse() as HttpWebResponse);
            }
            catch (WebException ex)
            {
                if (ex.Response is not HttpWebResponse response)
                {
                    request.Abort();
                    throw ex;
                }
                else
                {
                    return new HttpResponse(ex, request);
                }
            }
            catch (Exception ex)
            {
                request.Abort();
                throw ex;
            }
            finally
            {
                if (LoggerMgr.IsInfoEnabled)
                {
                    LoggerMgr.Info($"Send http request end, cost {(DateTime.Now.Ticks - reqTime.Ticks) / 10000} ms");
                }
            }
        }

        internal void PrepareRequestAndContext(HttpRequest request, HttpContext context)
        {
            var iheaders = GetIHeaders(context);
            CommonUtil.RenameHeaders(request, iheaders.HeaderPrefix(), iheaders.HeaderMetaPrefix());

            if (LoggerMgr.IsDebugEnabled)
            {
                LoggerMgr.Debug($"Perform {request.Method} request for {request.GetUrl()}");
                LoggerMgr.Debug("Perform http request with headers:" + CommonUtil.ConvertHeadersToString(request.Headers));
            }
        }

        internal HttpResponse PerformRequest(HttpRequest request, HttpContext context)
        {
            PrepareRequestAndContext(request, context);
            var response = PerformRequest(request, context, 0);
            foreach (var handler in context.Handlers)
            {
                handler.Handle(response);
            }
            return response;
        }

        internal HttpResponse PerformRequest(HttpRequest request, HttpContext context, int retryCount)
        {
            long originPos = -1;
            HttpResponse response = null;
            try
            {
                if (request.Content is { CanSeek: true })
                {
                    originPos = request.Content.Position;
                }
                response = DoRequest(request, context);

                new MergeResponseHeaderHandler(GetIHeaders(context)).Handle(response);

                var statusCode = Convert.ToInt32(response.StatusCode);

                if (LoggerMgr.IsDebugEnabled)
                {
                    LoggerMgr.Debug(
                        $"Response with statusCode {statusCode} and headers {CommonUtil.ConvertHeadersToString(response.Headers)}");
                }

                var maxErrorRetry = context.ObsConfig.MaxErrorRetry;

                switch (statusCode)
                {
                    case >= 300 and < 400 when statusCode != 304:
                    {
                        if (!response.Headers.TryGetValue(Constants.CommonHeaders.Location, out var location))
                            throw ParseObsException(response, "Try to redirect, but location is null!", context);
                        if (string.IsNullOrEmpty(location))
                            throw ParseObsException(response, "Try to redirect, but location is null!", context);
                        if (location.IndexOf("?") < 0)
                        {
                            location += "?" + CommonUtil.ConvertParamsToString(request.Params);
                        }
                        if (LoggerMgr.IsWarnEnabled)
                        {
                            LoggerMgr.Warn($"Redirect to {location}");
                        }
                        context.RedirectLocation = location;
                        retryCount--;
                        if (ShouldRetry(request, null, retryCount, maxErrorRetry))
                        {
                            PrepareRetry(request, response, retryCount, originPos, false);
                            return PerformRequest(request, context, ++retryCount);
                        }

                        if (retryCount > maxErrorRetry)
                        {
                            throw ParseObsException(response, "Exceeded 3xx redirect limit", context);
                        }
                        throw ParseObsException(response, "Try to redirect, but location is null!", context);
                    }
                    case >= 400 and < 500 or 304:
                    {
                        var exception = ParseObsException(response, "Request error", context);
                        if (!Constants.RequestTimeout.Equals(exception.ErrorCode)) throw exception;
                        if (ShouldRetry(request, null, retryCount, maxErrorRetry))
                        {
                            if (LoggerMgr.IsWarnEnabled)
                            {
                                LoggerMgr.Warn("Retrying connection that failed with RequestTimeout error");
                            }
                            PrepareRetry(request, response, retryCount, originPos, false);
                            return PerformRequest(request, context, ++retryCount);
                        }

                        if (retryCount > maxErrorRetry && LoggerMgr.IsErrorEnabled)
                        {
                            LoggerMgr.Error("Exceeded maximum number of retries for RequestTimeout errors");
                        }
                        throw exception;
                    }
                    case < 500:
                        return response;
                }

                if (ShouldRetry(request, null, retryCount, maxErrorRetry))
                {
                    PrepareRetry(request, response, retryCount, originPos, true);
                    return PerformRequest(request, context, ++retryCount);
                }

                if (retryCount > maxErrorRetry && LoggerMgr.IsErrorEnabled)
                {
                    LoggerMgr.Error("Encountered too many 5xx errors");
                }
                throw ParseObsException(response, "Request error", context);
            }
            catch (Exception ex)
            {
                try
                {
                    if (ex is ObsException)
                    {
                        if (LoggerMgr.IsErrorEnabled)
                        {
                            LoggerMgr.Error("Rethrowing as a ObsException error in PerformRequest", ex);
                        }
                        throw ex;
                    }
                    else
                    {
                        if (ShouldRetry(request, ex, retryCount, context.ObsConfig.MaxErrorRetry))
                        {
                            PrepareRetry(request, response, retryCount, originPos, true);
                            return PerformRequest(request, context, ++retryCount);
                        }
                        else if (retryCount > context.ObsConfig.MaxErrorRetry && LoggerMgr.IsWarnEnabled)
                        {
                            LoggerMgr.Warn("Too many errors excced the max error retry count", ex);
                        }
                        if (LoggerMgr.IsErrorEnabled)
                        {
                            LoggerMgr.Error("Rethrowing as a ObsException error in PerformRequest", ex);
                        }
                        throw ParseObsException(response, ex.Message, ex, context);
                    }
                }
                finally
                {
                    CommonUtil.CloseIDisposable(response);
                }
            }
        }

        private void PrepareRetry(HttpRequest request, HttpResponse response, int retryCount, long originPos, bool sleep)
        {
            CommonUtil.CloseIDisposable(response);

            if (request.Content != null && originPos >= 0 && request.Content.CanSeek)
            {
                request.Content.Seek(originPos, SeekOrigin.Begin);
                if (request.Content is TransferStream stream)
                {
                    stream.ResetReadProgress();
                }
            }

            if (!sleep) return;
            var delay = (int)Math.Pow(2, retryCount) * 50;
            if (LoggerMgr.IsWarnEnabled)
            {
                LoggerMgr.Warn($"Send http request error, will retry in {delay} ms");
            }
            Thread.Sleep(delay);
        }
        private ObsException ParseObsException(HttpResponse response, string message, HttpContext context) => 
            ParseObsException(response, message, null, context);

        private ObsException ParseObsException(HttpResponse? response, string message, Exception ex, HttpContext context)
        {
            var exception = new ObsException(message, ex);
            if (response != null)
            {
                exception.StatusCode = response.StatusCode;
                string temp;
                try
                {
                    if (response.Content.Length > 0)
                    {
                        CommonParser.ParseErrorResponse(response.Content, exception);
                    }
                    else if (response.Headers.ContainsKey(Constants.ObsHeadErrorCode) && response.Headers.ContainsKey(Constants.ObsHeadErrorMessage))
                    {
                        response.Headers.TryGetValue(Constants.ObsHeadErrorCode, out temp);
                        exception.ErrorCode = temp;
                        response.Headers.TryGetValue(Constants.ObsHeadErrorMessage, out temp);
                        exception.ErrorMessage = temp;
                    }
                    else
                    {
                        exception.ErrorCode = response.StatusCode.ToString();
                        exception.ErrorMessage = response.Failure.Message;
                    }
                }
                catch (Exception ee)
                {
                    exception.ErrorMessage = ee.Message;
                    if (LoggerMgr.IsErrorEnabled)
                    {
                        LoggerMgr.Error(ee.Message, ee);
                    }
                }

                if (response.Headers.TryGetValue(GetIHeaders(context).RequestId2Header(), out temp))
                {
                    exception.ObsId2 = temp;
                }
                if (string.IsNullOrEmpty(exception.RequestId) && response.Headers.TryGetValue(GetIHeaders(context).RequestIdHeader(), out temp))
                {
                    exception.RequestId = temp;
                }
                response.Abort();
            }
            exception.ErrorType = ErrorType.Receiver;
            return exception;
        }


        private bool ShouldRetry(HttpRequest request, Exception? ex, int retryCount, int maxErrorRetry) => 
            retryCount < maxErrorRetry && request.IsRepeatable && ex is null or IOException;

        private static void SetContent(HttpWebRequest webRequest,
                                      HttpRequest httpRequest,
                                      ObsConfig obsConfig)
        {
            var data = httpRequest.Content ?? new MemoryStream();

            long userSetContentLength = -1;
            if (httpRequest.Headers.TryGetValue(Constants.CommonHeaders.ContentLength, out var header))
            {
                userSetContentLength = long.Parse(header);
            }

            if (userSetContentLength >= 0)
            {
                webRequest.ContentLength = userSetContentLength;
                if (webRequest.ContentLength > Constants.DefaultStreamBufferThreshold)
                {
                    webRequest.AllowWriteStreamBuffering = false;
                }
            }
            else
            {
                webRequest.SendChunked = true;
                webRequest.AllowWriteStreamBuffering = false;
            }

            using var requestStream = webRequest.GetRequestStream();
            if (!webRequest.SendChunked)
            {
                CommonUtil.WriteTo(data, requestStream, webRequest.ContentLength, obsConfig.BufferSize);
            }
            else
            {
                CommonUtil.WriteTo(data, requestStream, obsConfig.BufferSize);
            }
        }

    }

    internal static class HttpWebRequestFactory
    {
        private static readonly object      _lock = new();
        private static volatile MethodInfo? _addHeaderInternal;
        private static readonly string      AddHeaderInternalMethodName = "AddInternal";


        public static bool ValidateCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            return true;
        }

        internal static HttpWebRequest BuildWebRequest(HttpRequest request, HttpContext context)
        {
            if (LoggerMgr.IsDebugEnabled)
            {
                LoggerMgr.Debug("Perform http request with url:" + request.GetUrl());
            }

            var url = string.IsNullOrEmpty(context.RedirectLocation) ? request.GetUrl() : context.RedirectLocation;
            var obsConfig = context.ObsConfig;
            var webRequest = (WebRequest.Create(url) as HttpWebRequest)!;

            AddHeaders(webRequest, request, obsConfig);
            AddProxy(webRequest, obsConfig);

            if (webRequest.RequestUri.Scheme.Equals("https") && !obsConfig.ValidateCertificate)
            {
                ServicePointManager.ServerCertificateValidationCallback = ValidateCertificate;
            }

            return webRequest;
        }

        [Obsolete("This method is obsolete, please directly use Add(string, string).",true)]
        private static MethodInfo? GetAddHeaderInternal()
        {
            if (_addHeaderInternal != null) return _addHeaderInternal;
            lock (_lock)
            {
                if (_addHeaderInternal == null)
                {
                    _addHeaderInternal = typeof(WebHeaderCollection).GetMethod(AddHeaderInternalMethodName, BindingFlags.NonPublic | BindingFlags.Instance,
                        null, [typeof(string), typeof(string)], null);
                }
            }

            return _addHeaderInternal;
        }


        private static void AddHeaders(HttpWebRequest webRequest, HttpRequest request,
                                              ObsConfig obsConfig)
        {

            webRequest.Timeout = obsConfig.Timeout;
            webRequest.ReadWriteTimeout = obsConfig.ReadWriteTimeout;
            webRequest.Method = request.Method.ToString();
            webRequest.AllowAutoRedirect = false;


#if !NETFRAMEWORK
            foreach (var header in request.Headers)
            {
                if (header.Key.Equals(Constants.CommonHeaders.ContentLength, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                webRequest.Headers.Add(header.Key, header.Value);
            }
#else
            try
            {
                webRequest.ServicePoint.ReceiveBufferSize = obsConfig.ReceiveBufferSize;
            }
            catch (Exception)
            {
                //ignore
            }
            foreach (var header in request.Headers)
            {
                if (header.Key.Equals(Constants.CommonHeaders.ContentLength, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                request.Headers.Add(header);
                //Directly use Add instead
                //GetAddHeaderInternal().Invoke(webRequest.Headers, new object[] { header.Key, header.Value });
            }
#endif
            webRequest.UserAgent = Constants.SdkUserAgent;
        }


        private static void AddProxy(HttpWebRequest webRequest, ObsConfig obsConfig)
        {

            webRequest.Proxy = null;

            if (string.IsNullOrEmpty(obsConfig.ProxyHost)) return;
            webRequest.Proxy = obsConfig.ProxyPort < 0 ? new WebProxy(obsConfig.ProxyHost) : new WebProxy(obsConfig.ProxyHost, obsConfig.ProxyPort);


            if (!string.IsNullOrEmpty(obsConfig.ProxyUserName))
            {
                webRequest.Proxy.Credentials = string.IsNullOrEmpty(obsConfig.ProxyDomain) ?
                    new NetworkCredential(obsConfig.ProxyUserName, obsConfig.ProxyPassword ?? string.Empty) :
                    new NetworkCredential(obsConfig.ProxyUserName, obsConfig.ProxyPassword ?? string.Empty,
                        obsConfig.ProxyDomain);
            }

            webRequest.PreAuthenticate = true;

            if (LoggerMgr.IsInfoEnabled)
            {

                LoggerMgr.Info($"Send http request using proxy {obsConfig.ProxyHost}:{obsConfig.ProxyPort}");
            }
        }

    }

}
