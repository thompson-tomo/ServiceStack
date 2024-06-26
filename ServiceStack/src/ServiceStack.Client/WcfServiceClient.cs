#if NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Xml;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Dispatcher;
using System.Threading;
using ServiceStack.Serialization;

namespace ServiceStack
{
    /// <summary>
    /// Adds the singleton instance of <see cref="CookieManagerMessageInspector"/> to an endpoint on the client.
    /// </summary>
    /// <remarks>
    /// Based on http://megakemp.wordpress.com/2009/02/06/managing-shared-cookies-in-wcf/
    /// </remarks>
    public class CookieManagerEndpointBehavior : IEndpointBehavior
    {
        public void AddBindingParameters(ServiceEndpoint endpoint, BindingParameterCollection bindingParameters) {}

        /// <summary>
        /// Adds the singleton of the <see cref="CookieManagerMessageInspector"/> class to the client endpoint's message inspectors.
        /// </summary>
        /// <param name="endpoint">The endpoint that is to be customized.</param>
        /// <param name="clientRuntime">The client runtime to be customized.</param>
        public void ApplyClientBehavior(ServiceEndpoint endpoint, ClientRuntime clientRuntime)
        {
            var cm = CookieManagerMessageInspector.Instance;
            cm.Uri = endpoint.ListenUri.AbsoluteUri;
            clientRuntime.MessageInspectors.Add(cm);
        }

        public void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher) {}

        public void Validate(ServiceEndpoint endpoint) {}
    }

    /// <summary>
    /// Maintains a copy of the cookies contained in the incoming HTTP response received from any service
    /// and appends it to all outgoing HTTP requests.
    /// </summary>
    /// <remarks>
    /// This class effectively allows to send any received HTTP cookies to different services,
    /// reproducing the same functionality available in ASMX Web Services proxies with the <see cref="System.Net.CookieContainer"/> class.
    /// Based on http://megakemp.wordpress.com/2009/02/06/managing-shared-cookies-in-wcf/
    /// </remarks>
    public class CookieManagerMessageInspector : IClientMessageInspector
    {
        private static CookieManagerMessageInspector instance;
        private readonly CookieContainer cookieContainer;
        public string Uri { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CookieManagerMessageInspector"/> class.
        /// </summary>
        public CookieManagerMessageInspector()
        {
            cookieContainer = new CookieContainer();
            Uri = "http://tempuri.org";
        }

        public CookieManagerMessageInspector(string uri)
        {
            cookieContainer = new CookieContainer();
            Uri = uri;
        }

        /// <summary>
        /// Gets the singleton <see cref="CookieManagerMessageInspector" /> instance.
        /// </summary>
        public static CookieManagerMessageInspector Instance => 
            instance ??= new CookieManagerMessageInspector();

        /// <summary>
        /// Inspects a message after a reply message is received but prior to passing it back to the client application.
        /// </summary>
        /// <param name="reply">The message to be transformed into types and handed back to the client application.</param>
        /// <param name="correlationState">Correlation state data.</param>
        public void AfterReceiveReply(ref Message reply, object correlationState)
        {
            var httpResponse = reply.Properties[HttpResponseMessageProperty.Name] as HttpResponseMessageProperty;

            var cookie = httpResponse?.Headers[HttpResponseHeader.SetCookie];
            if (!string.IsNullOrEmpty(cookie))
            {
                cookieContainer.SetCookies(new System.Uri(Uri), cookie);
            }
        }

        /// <summary>
        /// Inspects a message before a request message is sent to a service.
        /// </summary>
        /// <param name="request">The message to be sent to the service.</param>
        /// <param name="channel">The client object channel.</param>
        /// <returns>
        /// <strong>Null</strong> since no message correlation is used.
        /// </returns>
        public object BeforeSendRequest(ref Message request, IClientChannel channel)
        {
            // The HTTP request object is made available in the outgoing message only when
            // the Visual Studio Debugger is attacched to the running process
            if (!request.Properties.ContainsKey(HttpRequestMessageProperty.Name))
            {
                request.Properties.Add(HttpRequestMessageProperty.Name, new HttpRequestMessageProperty());
            }

            var httpRequest = (HttpRequestMessageProperty)request.Properties[HttpRequestMessageProperty.Name];
            httpRequest.Headers.Add(HttpRequestHeader.Cookie, cookieContainer.GetCookieHeader(new Uri(Uri)));

            return null;
        }
    }

    public abstract class WcfServiceClient : IWcfServiceClient
    {
        const string XPATH_SOAP_FAULT = "/s:Fault";
        const string XPATH_SOAP_FAULT_REASON = "/s:Fault/s:Reason";
        const string NAMESPACE_SOAP = "http://www.w3.org/2003/05/soap-envelope";
        const string NAMESPACE_SOAP_ALIAS = "s";
        public string WsdlServiceNamespace = "http://schemas.servicestack.net/types";

        public string Uri { get; set; }

        public abstract void SetProxy(Uri proxyAddress);
        protected abstract MessageVersion MessageVersion { get; }
        protected abstract Binding Binding { get; }

        /// <summary>
        /// Specifies if cookies should be stored
        /// </summary>
        // CCB Custom
        public bool StoreCookies { get; set; }

        public int Version { get; set; }
        public string SessionId { get; set; }
        public string BearerToken { get; set; }

        protected WcfServiceClient()
        {
            // CCB Custom
            this.StoreCookies = true;
        }

        private static XmlNamespaceManager GetNamespaceManager(XmlDocument doc)
        {
            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace(NAMESPACE_SOAP_ALIAS, NAMESPACE_SOAP);
            return nsmgr;
        }

        private static Exception CreateException(Exception e, XmlReader reader)
        {
            var doc = new XmlDocument();
            doc.Load(reader);
            var node = doc.SelectSingleNode(XPATH_SOAP_FAULT, GetNamespaceManager(doc));
            if (node != null)
            {
                string errMsg = null;
                var nodeReason = doc.SelectSingleNode(XPATH_SOAP_FAULT_REASON, GetNamespaceManager(doc));
                if (nodeReason != null)
                {
                    errMsg = nodeReason.FirstChild.InnerXml;
                }
                return new Exception($"SOAP FAULT '{errMsg}': {node.InnerXml}", e);
            }
            return e;
        }

        private ServiceEndpoint SyncReply
        {
            get
            {
                var contract = new ContractDescription("ServiceStack.ISyncReply", "http://services.servicestack.net/");
                var addr = new EndpointAddress(Uri);
                var endpoint = new ServiceEndpoint(contract, Binding, addr);
                return endpoint;
            }
        }

        public Message Send(object request)
        {
            this.PopulateRequestMetadata(request);

            return Send(request, request.GetType().Name);
        }

        public Message Send(object request, string action)
        {
            return Send(Message.CreateMessage(MessageVersion, action, request));
        }

        public Message Send(XmlReader reader, string action)
        {
            return Send(Message.CreateMessage(MessageVersion, action, reader));
        }

        public Message Send(Message message)
        {
            using (var client = new GenericProxy<ISyncReply>(SyncReply))
            {
                // CCB Custom...add behavior to propagate cookies across SOAP method calls
                if (StoreCookies)
                    client.ChannelFactory.Endpoint.Behaviors.Add(new CookieManagerEndpointBehavior());
                var response = client.Proxy.Send(message);
                return response;
            }
        }

        public static T GetBody<T>(Message message)
        {
            var buffer = message.CreateBufferedCopy(int.MaxValue);
            try
            {
                return buffer.CreateMessage().GetBody<T>();
            }
            catch (Exception ex)
            {
                throw CreateException(ex, buffer.CreateMessage().GetReaderAtBodyContents());
            }
        }

        public T Send<T>(object request)
        {
            Message responseMsg = null;
            try
            {
                responseMsg = Send(request);

                var requestType = request.GetType();

                var responseXml = GetMessageXml(responseMsg);
                var useXmlSerializerRequest = requestType.HasAttribute<XmlSerializerFormatAttribute>();

                var responseType = responseXml.StartsWith("<ErrorResponse")
                    ? typeof(ErrorResponse)
                    : typeof(T);

                var response = useXmlSerializerRequest
                    ? XmlSerializableSerializer.Instance.DeserializeFromString(responseXml, responseType)
                    : Serialization.DataContractSerializer.Instance.DeserializeFromString(responseXml, responseType);

                var responseStatus = response.GetResponseStatus();
                if (!string.IsNullOrEmpty(responseStatus?.ErrorCode))
                {
                    throw new WebServiceException(responseStatus.ErrorCode, null)
                    {
                        StatusCode = GetErrorStatus(responseMsg),
                        ResponseDto = response,
                        StatusDescription = responseStatus.ErrorCode,
                    };
                }

                return (T)response;
            }
            catch (WebServiceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var webEx = ex as WebException ?? ex.InnerException as WebException;
                if (webEx == null)
                {
                    throw new WebServiceException(ex.Message, ex)
                    {
                        StatusCode = 500,
                    };
                }

                var httpEx = webEx.Response as HttpWebResponse;
                throw new WebServiceException(webEx.Message, webEx)
                {
                    StatusCode = httpEx != null ? (int)httpEx.StatusCode : 500
                };
            }
        }

        private static string GetMessageXml(Message requestMsg)
        {
            string requestXml;
            using (var reader = requestMsg.GetReaderAtBodyContents())
            {
                requestXml = reader.ReadOuterXml();
            }
            return requestXml;
        }

        private int GetErrorStatus(Message responseMsg)
        {
            var errorStatus = 500;

            try
            {
                var statusCode = responseMsg.Headers.GetHeader<string>(
                    HttpHeaders.XStatus, WsdlServiceNamespace);
                if (statusCode != null)
                {
                    int.TryParse(statusCode, out errorStatus);
                }
            }
            catch (Exception) {}

            return errorStatus;
        }

        public TResponse Send<TResponse>(IReturn<TResponse> request)
        {
            return Send<TResponse>((object)request);
        }

        public void Send(IReturnVoid request)
        {
            Send<byte[]>(request);
        }

        public void Publish(object requestDto)
        {
            SendOneWay(requestDto);
        }

        public void PublishAll(IEnumerable<object> requestDtos)
        {
            throw new NotImplementedException();
        }

        public List<TResponse> SendAll<TResponse>(IEnumerable<object> requests)
        {
            throw new NotImplementedException();
        }

        public void AddHeader(string name, string value)
        {
            throw new NotImplementedException();
        }

        public void ClearCookies()
        {
            throw new NotImplementedException();
        }

        public Dictionary<string, string> GetCookieValues()
        {
            return new Dictionary<string, string>();
        }

        public void SetCookie(string name, string value, TimeSpan? expiresIn = null)
        {
            throw new NotImplementedException();
        }

        public void Get(IReturnVoid request)
        {
            throw new NotImplementedException();
        }

        public TResponse Get<TResponse>(IReturn<TResponse> requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Get<TResponse>(object requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Get<TResponse>(string relativeOrAbsoluteUrl)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<TResponse> GetLazy<TResponse>(IReturn<QueryResponse<TResponse>> queryDto)
        {
            throw new NotImplementedException();
        }

        public void Delete(IReturnVoid requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Delete<TResponse>(IReturn<TResponse> request)
        {
            throw new NotImplementedException();
        }

        public TResponse Delete<TResponse>(object request)
        {
            throw new NotImplementedException();
        }

        public TResponse Delete<TResponse>(string relativeOrAbsoluteUrl)
        {
            throw new NotImplementedException();
        }

        public void Post(IReturnVoid requestDto)
        {
            Send(requestDto);
        }

        public TResponse Post<TResponse>(IReturn<TResponse> requestDto)
        {
            return Send(requestDto);
        }

        public TResponse Post<TResponse>(object requestDto)
        {
            return Send<TResponse>(requestDto);
        }

        public TResponse Post<TResponse>(string relativeOrAbsoluteUrl, object request)
        {
            throw new NotImplementedException();
        }

        public void Put(IReturnVoid requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Put<TResponse>(IReturn<TResponse> requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Put<TResponse>(object requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Put<TResponse>(string relativeOrAbsoluteUrl, object requestDto)
        {
            throw new NotImplementedException();
        }

        public void Patch(IReturnVoid requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Patch<TResponse>(IReturn<TResponse> requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Patch<TResponse>(object requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Patch<TResponse>(string relativeOrAbsoluteUrl, object requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Send<TResponse>(string httpMethod, string relativeOrAbsoluteUrl, object request)
        {
            throw new NotImplementedException();
        }

        public TResponse PostFile<TResponse>(string relativeOrAbsoluteUrl, Stream fileToUpload, string fileName, string mimeType, string fieldName)
        {
            throw new NotImplementedException();
        }

        public void CustomMethod(string httpVerb, IReturnVoid requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse CustomMethod<TResponse>(string httpVerb, IReturn<TResponse> requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse CustomMethod<TResponse>(string httpVerb, object requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse PostFile<TResponse>(string relativeOrAbsoluteUrl, Stream fileToUpload, string fileName, string mimeType)
        {
            throw new NotImplementedException();
        }

        public TResponse PostFileWithRequest<TResponse>(
            Stream fileToUpload, string fileName, object request, string fieldName = "file")
        {
            throw new NotImplementedException();
        }

        public void SendOneWay(object requestDto)
        {
            SendOneWay(requestDto, requestDto.GetType().Name);
        }

        public void SendOneWay(string relativeOrAbsoluteUrl, object request)
        {
            SendOneWay(Message.CreateMessage(MessageVersion, relativeOrAbsoluteUrl, request));
        }

        public void SendAllOneWay(IEnumerable<object> requests)
        {
            throw new NotImplementedException();
        }

        public void SendOneWay(object requestDto, string action)
        {
            SendOneWay(Message.CreateMessage(MessageVersion, action, requestDto));
        }

        public void SendOneWay(XmlReader reader, string action)
        {
            SendOneWay(Message.CreateMessage(MessageVersion, action, reader));
        }

        public void SendOneWay(Message message)
        {
            using var client = new GenericProxy<IOneWay>(SyncReply);
            client.Proxy.SendOneWay(message);
        }

        public void SetCredentials(string userName, string password)
        {
            throw new NotImplementedException();
        }

        public Task<TResponse> GetAsync<TResponse>(IReturn<TResponse> requestDto, CancellationToken token = default) => throw new NotImplementedException();
        public Task<TResponse> GetAsync<TResponse>(object requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> GetAsync<TResponse>(string relativeOrAbsoluteUrl, CancellationToken token = default) => throw new NotImplementedException();

        public Task GetAsync(IReturnVoid requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> DeleteAsync<TResponse>(IReturn<TResponse> requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> DeleteAsync<TResponse>(object requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> DeleteAsync<TResponse>(string relativeOrAbsoluteUrl, CancellationToken token = default) => throw new NotImplementedException();

        public Task DeleteAsync(IReturnVoid requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> PostAsync<TResponse>(IReturn<TResponse> requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> PostAsync<TResponse>(object requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> PostAsync<TResponse>(string relativeOrAbsoluteUrl, object request, CancellationToken token = default) => throw new NotImplementedException();

        public Task PostAsync(IReturnVoid requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> PutAsync<TResponse>(IReturn<TResponse> requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> PutAsync<TResponse>(object requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> PutAsync<TResponse>(string relativeOrAbsoluteUrl, object request, CancellationToken token = default) => throw new NotImplementedException();

        public Task PutAsync(IReturnVoid requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> PatchAsync<TResponse>(IReturn<TResponse> requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> PatchAsync<TResponse>(object requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task PatchAsync(IReturnVoid requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> SendAsync<TResponse>(string httpMethod, string absoluteUrl, object request, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> CustomMethodAsync<TResponse>(string httpVerb, IReturn<TResponse> requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> CustomMethodAsync<TResponse>(string httpVerb, object requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task CustomMethodAsync(string httpVerb, IReturnVoid requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> CustomMethodAsync<TResponse>(string httpVerb, string relativeOrAbsoluteUrl, object request, CancellationToken token = default) => throw new NotImplementedException();

        public void CancelAsync()
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
        }

        public TResponse PostFileWithRequest<TResponse>(string relativeOrAbsoluteUrl, FileInfo fileToUpload, object request, string fieldName = "file")
        {
            throw new NotImplementedException();
        }

        public TResponse PostFileWithRequest<TResponse>(string relativeOrAbsoluteUrl, Stream fileToUpload, string fileName, object request, string fieldName = "file")
        {
            throw new NotImplementedException();
        }

        public TResponse PostFilesWithRequest<TResponse>(object request, IEnumerable<UploadFile> files)
        {
            throw new NotImplementedException();
        }

        public TResponse PostFilesWithRequest<TResponse>(string relativeOrAbsoluteUrl, object request, IEnumerable<UploadFile> files)
        {
            throw new NotImplementedException();
        }

        public Task<TResponse> SendAsync<TResponse>(object requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<TResponse> SendAsync<TResponse>(IReturn<TResponse> requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task<List<TResponse>> SendAllAsync<TResponse>(IEnumerable<object> requests, CancellationToken token = default) => throw new NotImplementedException();

        public Task SendAsync(IReturnVoid requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task PublishAsync(object requestDto, CancellationToken token = default) => throw new NotImplementedException();

        public Task PublishAllAsync(IEnumerable<object> requestDtos, CancellationToken token = default) => throw new NotImplementedException();
    }
}
#endif
