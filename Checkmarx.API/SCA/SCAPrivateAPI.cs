using Checkmarx.API;
using Checkmarx.API.SCA;

namespace Checkmarx.API.SCA
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System = global::System;

    public partial class Client
    {
        /// <param name="projectId">Project name</param>
        /// <returns>Success</returns>
        /// <exception cref="ApiException">A server side error occurred.</exception>
        public System.Threading.Tasks.Task<string> RecalculateAsync(Guid projectId)
        {
            return RecalculateAsync(projectId, System.Threading.CancellationToken.None);
        }

        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <param name="projectId">Project name</param>
        /// <returns>Success</returns>
        /// <exception cref="ApiException">A server side error occurred.</exception>
        public async System.Threading.Tasks.Task<string> RecalculateAsync(Guid projectId, System.Threading.CancellationToken cancellationToken)
        {
            var urlBuilder_ = new System.Text.StringBuilder();
            urlBuilder_.Append(BaseUrl != null ? BaseUrl.TrimEnd('/') : "").Append("/scan-runner/scans/recalculate?");
            if (projectId != null)
            {
                urlBuilder_.Append(System.Uri.EscapeDataString("projectId") + "=").Append(System.Uri.EscapeDataString(ConvertToString(projectId, System.Globalization.CultureInfo.InvariantCulture))).Append("&");
            }
            urlBuilder_.Length--;

            var client_ = _httpClient;
            var disposeClient_ = false;
            try
            {
                using (var multipartFormDataContent = new MultipartFormDataContent())
                {
                    multipartFormDataContent.Headers.Add("cxorigin", "WebApp");
                    multipartFormDataContent.Headers.Add("origin", "https://eu.sca.checkmarx.net");
                    
                    var values = new[]
                    {
                        new KeyValuePair<string, string>("projectId", projectId.ToString()),
                    };

                    foreach (var keyValuePair in values)
                    {
                        multipartFormDataContent.Add(new StringContent(keyValuePair.Value),
                            String.Format("\"{0}\"", keyValuePair.Key));
                    }

                    var response_ = await client_.PostAsync(new System.Uri(urlBuilder_.ToString(), System.UriKind.RelativeOrAbsolute), multipartFormDataContent, cancellationToken).ConfigureAwait(false);

                    var disposeResponse_ = true;
                    try
                    {
                        var headers_ = System.Linq.Enumerable.ToDictionary(response_.Headers, h_ => h_.Key, h_ => h_.Value);
                        if (response_.Content != null && response_.Content.Headers != null)
                        {
                            foreach (var item_ in response_.Content.Headers)
                                headers_[item_.Key] = item_.Value;
                        }

                        ProcessResponse(client_, response_);

                        var status_ = (int)response_.StatusCode;
                        if (status_ == 201)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<String>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            return objectResponse_.Object;
                        }
                        else
                        if (status_ == 404)
                        {
                            string responseText_ = (response_.Content == null) ? string.Empty : await response_.Content.ReadAsStringAsync().ConfigureAwait(false);
                            throw new ApiException("Not found", status_, responseText_, headers_, null);
                        }
                        else
                        {
                            var responseData_ = response_.Content == null ? null : await response_.Content.ReadAsStringAsync().ConfigureAwait(false);
                            throw new ApiException("The HTTP status code of the response was not expected (" + status_ + ").", status_, responseData_, headers_, null);
                        }
                    }
                    finally
                    {
                        if (disposeResponse_)
                            response_.Dispose();
                    }
                }
             
            }
            catch(Exception e)
            {
                throw e;
            }
            finally
            {
                if (disposeClient_)
                    client_.Dispose();
            }
        }
    }
}
