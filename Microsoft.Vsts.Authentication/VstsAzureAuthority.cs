﻿/**** Git Credential Manager for Windows ****
 *
 * Copyright (c) Microsoft Corporation
 * All rights reserved.
 *
 * MIT License
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the """"Software""""), to deal
 * in the Software without restriction, including without limitation the rights to
 * use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
 * the Software, and to permit persons to whom the Software is furnished to do so,
 * subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
 * FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
 * COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN
 * AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
 * WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."
**/

using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Microsoft.Alm.Authentication
{
    internal class VstsAzureAuthority : AzureAuthority, IVstsAuthority
    {
        public VstsAzureAuthority(RuntimeContext context, string authorityHostUrl = null)
            : base(context)
        {
            AuthorityHostUrl = authorityHostUrl ?? AuthorityHostUrl;
        }

        public async Task<Token> GeneratePersonalAccessToken(TargetUri targetUri, Token accessToken, VstsTokenScope tokenScope, bool requireCompactToken, TimeSpan? tokenDuration = null)
        {
            if (targetUri is null)
                throw new ArgumentNullException(nameof(targetUri));
            if (accessToken is null)
                throw new ArgumentNullException(nameof(accessToken));
            if (tokenScope is null)
                throw new ArgumentNullException(nameof(tokenScope));

            try
            {
                var requestUri = await CreatePersonalAccessTokenRequestUri(targetUri, accessToken, requireCompactToken);
                var options = new NetworkRequestOptions(true)
                {
                    Authorization = accessToken,
                };

                using (StringContent content = GetAccessTokenRequestBody(targetUri, tokenScope, tokenDuration))
                using (var response = await Network.HttpPostAsync(requestUri, content, options))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        string responseText = await response.Content.ReadAsStringAsync();

                        if (!string.IsNullOrWhiteSpace(responseText))
                        {
                            // Find the 'token : <value>' portion of the result content, if any.
                            Match tokenMatch = null;
                            if ((tokenMatch = Regex.Match(responseText, @"\s*""token""\s*:\s*""([^\""]+)""\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)).Success)
                            {
                                string tokenValue = tokenMatch.Groups[1].Value;
                                Token token = new Token(tokenValue, TokenType.Personal);

                                Trace.WriteLine($"personal access token acquisition for '{targetUri}' succeeded.");

                                return token;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"! an error occurred: {e.Message}");
            }

            Trace.WriteLine($"personal access token acquisition for '{targetUri}' failed.");

            return null;
        }

        public async Task<bool> PopulateTokenTargetId(TargetUri targetUri, Token accessToken)
        {
            if (targetUri is null)
                throw new ArgumentNullException(nameof(targetUri));
            if (accessToken is null)
                throw new ArgumentNullException(nameof(accessToken));

            string resultId = null;

            try
            {
                // Create an request to the VSTS deployment data end-point.
                var requestUri = GetConnectionDataUri(targetUri);
                var options = new NetworkRequestOptions(true)
                {
                    Authorization = accessToken,
                };

                // Send the request and wait for the response.
                using (var response = await Network.HttpGetAsync(requestUri, options))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        string content = await response.Content.ReadAsStringAsync();
                        Match match;

                        if ((match = Regex.Match(content, @"""instanceId""\s*\:\s*""([^""]+)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)).Success
                            && match.Groups.Count == 2)
                        {
                            resultId = match.Groups[1].Value;
                        }
                    }
                }
            }
            catch (HttpRequestException exception)
            {
                Trace.WriteLine($"server returned '{exception.Message}'.");
            }

            if (Guid.TryParse(resultId, out Guid instanceId))
            {
                Trace.WriteLine($"target identity is '{resultId}'.");
                accessToken.TargetIdentity = instanceId;

                return true;
            }

            return false;
        }

        public async Task<bool> ValidateCredentials(TargetUri targetUri, Credential credentials)
        {
            if (targetUri is null)
                throw new ArgumentNullException(nameof(targetUri));
            if (credentials is null)
                throw new ArgumentNullException(nameof(credentials));

            try
            {
                // Create an request to the VSTS deployment data end-point.
                var requestUri = GetConnectionDataUri(targetUri);
                var options = new NetworkRequestOptions(true)
                {
                    Authorization = credentials,
                };

                // Send the request and wait for the response.
                using (var response = await Network.HttpGetAsync(requestUri, options))
                {
                    if (response.IsSuccessStatusCode)
                        return true;

                    // Even if the service responded, if the issue isn't a 400 class response then
                    // the credentials were likely not rejected.
                    if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                        return true;
                }
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"! error: '{exception.Message}'.");
            }

            Trace.WriteLine($"credential validation for '{targetUri}' failed.");
            return false;
        }

        public async Task<bool> ValidateToken(TargetUri targetUri, Token token)
        {
            if (targetUri is null)
                throw new ArgumentNullException(nameof(targetUri));
            if (token is null)
                throw new ArgumentNullException(nameof(token));

            // Personal access tokens are effectively credentials, treat them as such.
            if (token.Type == TokenType.Personal)
                return await ValidateCredentials(targetUri, (Credential)token);

            try
            {
                // Create an request to the VSTS deployment data end-point.
                var requestUri = GetConnectionDataUri(targetUri);
                var options = new NetworkRequestOptions(true)
                {
                    Authorization = token,
                };

                // Send the request and wait for the response.
                using (var response = await Network.HttpGetAsync(requestUri, options))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        // Even if the service responded, if the issue isn't a 400 class response then
                        // the credentials were likely not rejected.
                        if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                            return false;

                        Trace.WriteLine($"unable to validate credentials due to '{response.StatusCode}'.");
                        return true;
                    }
                }
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"! error: '{exception.Message}'.");
            };


            Trace.WriteLine($"token validation for '{targetUri}' failed.");
            return false;
        }

        internal static TargetUri GetConnectionDataUri(TargetUri targetUri)
        {
            const string VstsValidationUrlPath = "_apis/connectiondata";

            if (targetUri is null)
                throw new ArgumentNullException(nameof(targetUri));

            string requestUrl = targetUri.ToString(false, true, false);

            if (targetUri.TargetUriContainsUsername)
            {
                string escapedUserInfo = Uri.EscapeUriString(targetUri.TargetUriUsername);

                requestUrl = requestUrl + escapedUserInfo + "/";
            }

            // Create a URL to the connection data end-point, it's deployment level and "always on".
            string validationUrl = requestUrl + VstsValidationUrlPath;

            return new TargetUri(validationUrl, targetUri.ProxyUri?.ToString());
        }

        internal async Task<TargetUri> GetIdentityServiceUri(TargetUri targetUri, Secret authorization)
        {
            const string LocationServiceUrlPathAndQuery = "_apis/ServiceDefinitions/LocationService2/951917AC-A960-4999-8464-E3F0AA25B381?api-version=1.0";

            if (targetUri is null)
                throw new ArgumentNullException(nameof(targetUri));
            if (authorization is null)
                throw new ArgumentNullException(nameof(authorization));

            string tenantUrl = targetUri.ToString(false, true, false);

            if (targetUri.TargetUriContainsUsername)
            {
                string escapedUserInfo = Uri.EscapeUriString(targetUri.TargetUriUsername);
                tenantUrl = tenantUrl + escapedUserInfo + "/";
            }

            var locationServiceUrl = tenantUrl + LocationServiceUrlPathAndQuery;
            var requestUri = new TargetUri(locationServiceUrl, targetUri.ProxyUri?.ToString());
            var options = new NetworkRequestOptions(true)
            {
                Authorization = authorization,
            };

            try
            {
                using (var response = await Network.HttpGetAsync(requestUri, options))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        using (HttpContent content = response.Content)
                        {
                            string responseText = await content.ReadAsStringAsync();

                            Match match;
                            if ((match = Regex.Match(responseText, @"\""location\""\:\""([^\""]+)\""", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)).Success)
                            {
                                string identityServiceUrl = match.Groups[1].Value;
                                var idenitityServiceUri = new Uri(identityServiceUrl, UriKind.Absolute);

                                return new TargetUri(idenitityServiceUri, targetUri.ProxyUri);
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"! error: '{exception.Message}'.");
                throw new VstsLocationServiceException($"Failed to find Identity Service for `{targetUri}`.", exception);
            }

            return null;
        }

        private StringContent GetAccessTokenRequestBody(TargetUri targetUri, VstsTokenScope tokenScope, TimeSpan? duration = null)
        {
            const string ContentBasicJsonFormat = "{{ \"scope\" : \"{0}\", \"displayName\" : \"Git: {1} on {2}\" }}";
            const string ContentTimedJsonFormat = "{{ \"scope\" : \"{0}\", \"displayName\" : \"Git: {1} on {2}\", \"validTo\": \"{3:u}\" }}";
            const string HttpJsonContentType = "application/json";

            if (targetUri is null)
                throw new ArgumentNullException(nameof(targetUri));
            if (tokenScope is null)
                throw new ArgumentNullException(nameof(tokenScope));

            string tokenUrl = targetUri.ToString(false, true, false);

            if (targetUri.TargetUriContainsUsername)
            {
                string escapedUserInfo = Uri.EscapeUriString(targetUri.TargetUriUsername);
                tokenUrl = tokenUrl + escapedUserInfo + "/";
            }

            Trace.WriteLine($"creating access token scoped to '{tokenScope}' for '{targetUri}'");

            string jsonContent = (duration.HasValue && duration.Value > TimeSpan.FromHours(1))
                ? string.Format(ContentTimedJsonFormat, tokenScope, tokenUrl, Environment.MachineName, DateTime.UtcNow + duration.Value)
                : string.Format(ContentBasicJsonFormat, tokenScope, tokenUrl, Environment.MachineName);
            StringContent content = new StringContent(jsonContent, Encoding.UTF8, HttpJsonContentType);

            return content;
        }

        private async Task<TargetUri> CreatePersonalAccessTokenRequestUri(TargetUri targetUri, Secret authorization, bool requireCompactToken)
        {
            const string SessionTokenUrl = "_apis/token/sessiontokens?api-version=1.0";
            const string CompactTokenUrl = SessionTokenUrl + "&tokentype=compact";

            if (targetUri is null)
                throw new ArgumentNullException(nameof(targetUri));
            if (authorization is null)
                throw new ArgumentNullException(nameof(authorization));

            var idenityServiceUri = await GetIdentityServiceUri(targetUri, authorization);

            if (idenityServiceUri is null)
                throw new VstsLocationServiceException($"Failed to find Identity Service for `{targetUri}`.");

            string url = idenityServiceUri.ToString();

            url += requireCompactToken
                ? CompactTokenUrl
                : SessionTokenUrl;

            var requestUri = new Uri(url, UriKind.Absolute);

            return new TargetUri(requestUri, targetUri.ProxyUri);
        }
    }
}
