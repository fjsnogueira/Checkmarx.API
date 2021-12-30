﻿using Capri;
using Checkmarx.API.OSA;
using Checkmarx.API.SAST;
using CxAuditWebService;
using CxDataRepository;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PortalSoap;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Project = CxDataRepository.Project;
using Scan = Checkmarx.API.SAST.Scan;

namespace Checkmarx.API
{
    /// <summary>
    /// Wrapper for accessing the Checkmarx Products
    /// </summary>
    public class CxClient : IDisposable
    {
        /// <summary>
        /// REST API client
        /// </summary>
        private HttpClient httpClient;

        /// <summary>
        /// OData client
        /// </summary>
        private Default.Container _oData;

        private DefaultV9.Container _oDataV9;

        private List<CxDataRepository.Project> _odp;
        private List<CxDataRepository.Project> _oDataProjs
        {
            get
            {
                if (_odp == null)
                {
                    _odp = _isV9 ? _oDataV9.Projects.ToList() : _oData.Projects.ToList();
                }
                return _odp;
            }
        }

        private global::Microsoft.OData.Client.DataServiceQuery<global::CxDataRepository.Scan> _oDataScans => _isV9 ? _oDataV9.Scans.Expand(x => x.ScannedLanguages) : _oData.Scans.Expand(x => x.ScannedLanguages);

        private global::Microsoft.OData.Client.DataServiceQuery<global::CxDataRepository.Project> _oDataProjects => _isV9 ? _oDataV9.Projects : _oData.Projects;

        /// <summary>
        /// SOAP client
        /// </summary>
        private PortalSoap.CxPortalWebServiceSoapClient _cxPortalWebServiceSoapClient;


        private cxPortalWebService93.CxPortalWebServiceSoapClient _cxPortalWebServiceSoapClientV9;

        private CxAuditWebService.CxAuditWebServiceSoapClient _cxAuditWebServiceSoapClient;

        private Dictionary<long, CxDataRepository.Scan> _scanCache;

        /// <summary>
        /// Get a preset for a specific scan.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public string GetScanPreset(long id)
        {
            checkConnection();

            if (id <= 0)
            {
                throw new ArgumentException(nameof(id));
            }

            if (_scanCache == null)
            {
#if DEBUG
                var watch = new Stopwatch();
                watch.Start();
#endif

                _scanCache = _oDataScans.Where(x => x.IsLocked).ToDictionary(x => x.Id);

#if DEBUG
                watch.Stop();
                Console.WriteLine($"Found {_scanCache.Keys.Count} scans in {watch.Elapsed.TotalMinutes} minutes");
                Console.SetCursorPosition(0, Console.CursorTop);
#endif
            }

            if (!_scanCache.ContainsKey(id))
            {
                var scan = _oDataScans.Where(x => x.Id == id).FirstOrDefault();
                if (scan != null)
                {
                    _scanCache.Add(scan.Id, scan);
                    return scan.PresetName;
                }

                throw new KeyNotFoundException($"Scan with Id {id} does not exist.");
            }

            return _scanCache[id].PresetName;
        }

        /// <summary>
        /// Check if the wrapper is connected.
        /// </summary>
        public bool Connected
        {
            get
            {
                if (httpClient == null || (_jwtSecurityToken.ValidTo - DateTime.UtcNow).TotalMinutes < 5) // login and re-logins...
                {
                    httpClient = Login(SASTServerURL.ToString(), Username, Password);
                }

                return true;
            }
        }

        /// <summary>
        /// URI of the server.
        /// </summary>
        public Uri SASTServerURL { get; }

        public string Username { get; }

        public string Password { get; }

        /// <summary>
        /// Localization Id
        /// </summary>
        public int LcId { get; private set; } = 1033;

        /// <summary>
        /// Constructor of the Checkmarx Client
        /// </summary>
        /// <param name="sastServerAddress">Server URL, e.g. http://localhost/</param>
        /// <param name="username">The username of the user, if it is an LDAP user please put the DOMAIN\Username</param>
        /// <param name="password">The password of the user</param>
        /// <param name="lcid">Localization of the user</param>
        public CxClient(Uri sastServerAddress, string username, string password, int lcid = 1033)
        {
            Username = username;
            Password = password;
            SASTServerURL = sastServerAddress;
            LcId = lcid;
        }

        //public CxClient(Uri webServerAddress, AuthenticationHeaderValue authenticationHeaderValue)
        //{
        //    WebServerURL = webServerAddress;
        //    AuthenticationToken = authenticationHeaderValue;
        //}

        private JwtSecurityToken _jwtSecurityToken;

        private AuthenticationHeaderValue _authenticationHeaderValue = null;

        private string _soapSessionId = "";

        /// <summary>
        /// Authentication Token for the Version REST 8.9, and all service 9.X
        /// </summary>
        public AuthenticationHeaderValue AuthenticationToken
        {
            get
            {
                if (_authenticationHeaderValue != null)
                    return _authenticationHeaderValue;

                // Connect and get the _authentication header
                if (Connected)
                    return _authenticationHeaderValue;

                return _authenticationHeaderValue;
            }
            private set
            {
                if (_authenticationHeaderValue != value)
                {
                    _authenticationHeaderValue = value;

                    if (_authenticationHeaderValue != null)
                    {
                        var webServer = SASTServerURL;
                        if (webServer.LocalPath != "cxrestapi")
                        {
                            webServer = new Uri(webServer, "/cxrestapi/");
                        }

                        httpClient = new HttpClient
                        {
                            BaseAddress = webServer
                        };

                        _jwtSecurityToken = new JwtSecurityToken(_authenticationHeaderValue.Parameter);
                        httpClient.DefaultRequestHeaders.Authorization = _authenticationHeaderValue;
                    }
                }
            }
        }

        private string _version;
        /// <summary>
        /// Get SAST Version
        /// </summary>
        public string Version
        {
            get
            {
                checkConnection();
                return _version;
            }
        }

        private bool _isV9 = false;

        #region Access Control 

        private HttpClient Login(string baseURL = "http://localhost/cxrestapi/",
            string userName = "", string password = "")
        {
            var webServer = new Uri(baseURL);

            Uri baseServer = new Uri(webServer.AbsoluteUri);

            _cxPortalWebServiceSoapClient = new PortalSoap.CxPortalWebServiceSoapClient(
                baseServer, TimeSpan.FromSeconds(60), userName, password);

            _version = _cxPortalWebServiceSoapClient.GetVersionNumber().Version;

            _isV9 = _version.StartsWith("V 9.");

            Console.WriteLine("Checkmarx " + _version);

            var httpClient = new HttpClient
            {
                BaseAddress = webServer
            };

            if (httpClient.BaseAddress.LocalPath != "cxrestapi")
            {
                httpClient.BaseAddress = new Uri(webServer, "/cxrestapi/");
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post, "auth/identity/connect/token"))
            {
                request.Headers.Add("Accept", "application/json");

                var values = new List<KeyValuePair<string, string>> {
                    new KeyValuePair<string, string>("username", userName),
                    new KeyValuePair<string, string>("password", password),
                    new KeyValuePair<string, string>("grant_type", "password"),
                    new KeyValuePair<string, string>("scope",
                    _isV9 ? "offline_access sast_api" : "sast_rest_api"),
                    new KeyValuePair<string, string>("client_id",
                    _isV9 ? "resource_owner_sast_client" : "resource_owner_client"),
                    new KeyValuePair<string, string>("client_secret", "014DF517-39D1-4453-B7B3-9930C563627C")
                };

                request.Content = new FormUrlEncodedContent(values);

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    JObject accessToken = JsonConvert.DeserializeObject<JObject>(response.Content.ReadAsStringAsync().Result);

                    string authToken = ((JProperty)accessToken.First).Value.ToString();

                    if (_isV9)
                    {
                        #region V8

                        _cxPortalWebServiceSoapClient = new PortalSoap.CxPortalWebServiceSoapClient(baseServer, TimeSpan.FromSeconds(60), userName, password);

                        var portalChannelFactory = _cxPortalWebServiceSoapClient.ChannelFactory;
                        portalChannelFactory.UseMessageInspector(async (request, channel, next) =>
                        {
                            HttpRequestMessageProperty reqProps = new HttpRequestMessageProperty();
                            reqProps.Headers.Add("Authorization", $"Bearer {authToken}");
                            request.Properties.Add(HttpRequestMessageProperty.Name, reqProps);
                            var response = await next(request);
                            return response;
                        });

                        #endregion

                        #region V9

                        _cxPortalWebServiceSoapClientV9 = new cxPortalWebService93.CxPortalWebServiceSoapClient(baseServer, TimeSpan.FromSeconds(60), userName, password);

                        var portalChannelFactoryV9 = _cxPortalWebServiceSoapClientV9.ChannelFactory;
                        portalChannelFactoryV9.UseMessageInspector(async (request, channel, next) =>
                        {
                            HttpRequestMessageProperty reqProps = new HttpRequestMessageProperty();
                            reqProps.Headers.Add("Authorization", $"Bearer {authToken}");
                            request.Properties.Add(HttpRequestMessageProperty.Name, reqProps);
                            var response = await next(request);
                            return response;
                        });

                        _cxAuditWebServiceSoapClient = new CxAuditWebServiceSoapClient(baseServer, TimeSpan.FromSeconds(60), userName, password);
                        var auditChannelFactory = _cxAuditWebServiceSoapClient.ChannelFactory;
                        auditChannelFactory.UseMessageInspector(async (request, channel, next) =>
                        {
                            HttpRequestMessageProperty reqProps = new HttpRequestMessageProperty();
                            reqProps.Headers.Add("Authorization", $"Bearer {authToken}");
                            request.Properties.Add(HttpRequestMessageProperty.Name, reqProps);
                            var response = await next(request);
                            return response;
                        });

                        #endregion

                        // ODATA V9
                        _oDataV9 = CxOData.ConnectToODataV9(webServer, authToken);
                    }
                    else
                    {
                        _soapSessionId = _cxPortalWebServiceSoapClient.Login(new PortalSoap.Credentials
                        {
                            User = userName,
                            Pass = password
                        }, LcId).SessionId;

                        // ODATA V8
                        _oData = CxOData.ConnectToOData(webServer, userName, password);
                    }

                    AuthenticationToken = new AuthenticationHeaderValue("Bearer", authToken);

                    httpClient.DefaultRequestHeaders.Authorization = AuthenticationToken;

                    return httpClient;
                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }
        }

        public string GetProjectTeamName(string teamId)
        {
            return GetTeams()[teamId];
        }

        public PortalSoap.CxWSResponseServerLicenseData GetLicense()
        {
            checkConnection();

            return _cxPortalWebServiceSoapClient.GetServerLicenseData(_soapSessionId);
        }

        // Cache
        private Dictionary<string, string> _teamsCache;

        public string GetProjectTeamName(int projectId)
        {
            return GetProjectTeamName(GetProjectTeamId(projectId));
        }

        public Dictionary<string, string> GetTeams()
        {
            checkConnection();

            if (_teamsCache != null)
                return _teamsCache;

            using (var request = new HttpRequestMessage(HttpMethod.Get, "auth/teams"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    _teamsCache = new Dictionary<string, string>();

                    foreach (var item in (JArray)JsonConvert.DeserializeObject(response.Content.ReadAsStringAsync().Result))
                    {
                        if (!_teamsCache.ContainsKey(item.SelectToken("id").ToString()))
                        {
                            _teamsCache.Add(
                               item.SelectToken("id").ToString(),
                               (string)item.SelectToken("fullName"));
                        }
                    }

                    return _teamsCache;
                }

                throw new NotSupportedException(request.ToString());
            }
        }

        public IEnumerable<Project> GetProjectsWithLastScan()
        {
            checkConnection();

            return _oDataProjects.Expand(x => x.LastScan);
        }

        public IQueryable<CxDataRepository.Scan> GetScansFromOData(long projectId)
        {
            checkConnection();
            return _oDataScans.Expand(x => x.ScannedLanguages).Where(x => x.ProjectId == projectId);
        }

        #endregion

        private void checkConnection()
        {
            if (!Connected)
                throw new NotSupportedException();
        }

        /// <summary>
        /// Gets the excluded settings.
        /// </summary>
        /// <param name="projectId">The project identifier.</param>
        /// <returns>excludedFoldersPattern, excludedFilesPattern</returns>
        /// <exception cref="NotSupportedException">
        /// </exception>
        public Tuple<string, string> GetExcludedSettings(int projectId)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"projects/{projectId}/sourceCode/excludeSettings"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    JObject excludeSettings = JsonConvert.DeserializeObject<JObject>(response.Content.ReadAsStringAsync().Result);

                    return new Tuple<string, string>(
                       (string)excludeSettings.SelectToken("excludeFoldersPattern"),
                       (string)excludeSettings.SelectToken("excludeFilesPattern"));
                }
            }

            throw new NotSupportedException();
        }

        public void SetProjectSettings(int projectId, int presetId, int engineConfigurationId, int scanActionId)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"sast/scanSettings"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                JObject settings = new JObject
                {
                    { "projectId", projectId },
                    { "presetId", presetId },
                    { "engineConfigurationId", engineConfigurationId },
                    { "postScanActionId", scanActionId },
                };

                JObject emailNotifications = new JObject
                {
                    {
                        "failedScan",
                        new JArray
                        {
                            "test@cx.com"
                        }
                    }
                };

                settings.Add("emailNotifications", emailNotifications);

                request.Content = new StringContent(JsonConvert.SerializeObject(settings));

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {

                }
            }

        }


        /// <summary>
        /// Creates Manage Fields in SAST.
        /// </summary>
        /// <param name="scanId">Name of the Fields</param>
        public byte[] GetSourceCode(long scanID)
        {
            checkConnection();

            var result = _cxAuditWebServiceSoapClient.GetSourceCodeForScanAsync(_soapSessionId, scanID).Result;
            if (!result.IsSuccesfull)
            {
                throw new Exception(result.ErrorMessage);
            }

            return result.sourceCodeContainer.ZippedFile;
        }

        /// <summary>
        /// Creates Manage Fields in SAST.
        /// </summary>
        /// <param name="fieldNames">Name of the Fields</param>
        public void CreateCustomField(params string[] fieldNames)
        {
            if (fieldNames == null)
                throw new ArgumentNullException(nameof(fieldNames));

            checkConnection();

            var existingFields = GetSASTCustomFields();
            if (existingFields.Count >= 10 || (existingFields.Count + fieldNames.Count()) > 10)
            {
                throw new Exception("Only 10 custom fields can be defined.");
            }

            var fields = new List<PortalSoap.CxWSCustomField>();
            foreach (var name in fieldNames)
            {
                fields.Add(new PortalSoap.CxWSCustomField { Name = name });
            }

            var result = _cxPortalWebServiceSoapClient.SaveCustomFields(_soapSessionId, fields.ToArray());
            if (!result.IsSuccesfull)
            {
                throw new Exception(result.ErrorMessage);
            }
        }

        public void SetCustomFields(ProjectDetails projDetails, IEnumerable<CustomField> customFields)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Put, $"projects/{projDetails.Id}"))
            {
                request.Headers.Add("Accept", "application/json;v=2.0");
                JObject settings = new JObject
                {
                    { "name",  projDetails.Name },
                    { "owningTeam", projDetails.TeamId.ToString() },
                    { "customFields", new JArray(
                        customFields.Select(x => new JObject
                        {
                            {  "id", x.Id },
                            {  "value", x.Value }
                        }))
                    }
                };

                request.Content = new StringContent(JsonConvert.SerializeObject(settings));

                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode != HttpStatusCode.NoContent)
                {
                    throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
                }
            }
        }

        public DateTimeOffset GetProjectCreationDate(long projectID)
        {
            checkConnection();

            return _oDataProjs.Where(x => x.Id == projectID).First().CreatedDate;
        }

        public ProjectDetails GetProjectSettings(int projectId)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"projects/{projectId}"))
            {
                request.Headers.Add("Accept", "application/json;v=2.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    ProjectDetails projDetails = JsonConvert.DeserializeObject<ProjectDetails>(response.Content.ReadAsStringAsync().Result);

                    return projDetails;

                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }
        }

        /// <summary>
        /// Deletes an existing project with all related scans
        /// </summary>
        /// <param name="projectId"></param>
        /// <param name="deleteRunningScans"></param>
        public void DeleteProject(int projectId, bool deleteRunningScans = true)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Delete, $"projects/{projectId}"))
            {
                request.Headers.Add("Accept", "application/json;v=2.0");

                JObject settings = new JObject
                {
                    { "deleteRunningScans",  deleteRunningScans },
                };

                request.Content = new StringContent(JsonConvert.SerializeObject(settings));

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
                }
            }
        }

        /// <summary>
        /// For V8.9 is UID, for 9.X or higher is Int.
        /// </summary>
        /// <param name="projectId"></param>
        /// <returns></returns>
        public string GetProjectTeamId(int projectId)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"projects/{projectId}"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    JObject excludeSettings = JsonConvert.DeserializeObject<JObject>(response.Content.ReadAsStringAsync().Result);

                    return (string)excludeSettings.SelectToken("teamId");

                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }
        }

        public Dictionary<string, int> GetSASTCustomFields()
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"customFields"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                Dictionary<string, int> customFields = new Dictionary<string, int>();

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string customFieldsArray = response.Content.ReadAsStringAsync().Result;

                    var projectList = (JArray)JsonConvert.DeserializeObject(customFieldsArray);

                    foreach (var item in projectList)
                    {
                        customFields.Add((string)item.SelectToken("name"),
                            (int)item.SelectToken("id"));
                    }

                    return customFields;
                }
            }

            throw new NotSupportedException();
        }

        public Dictionary<string, CustomField> GetProjectCustomFields(int projectId)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"projects/{projectId}"))
            {
                request.Headers.Add("Accept", "application/json;v=2.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    ProjectDetails projectSettings = JsonConvert.DeserializeObject<ProjectDetails>(response.Content.ReadAsStringAsync().Result);

                    return projectSettings.CustomFields.ToDictionary(x => x.Name);
                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }
        }

        #region OSA

        public IEnumerable<Guid> GetOSAScansIds(int projectId)
        {
            return GetOSAScans(projectId).Select(x => x.Id);
        }

        public ICollection<OSAScanDto> GetOSAScans(int projectId)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"osa/scans?projectId={projectId}"))
            {
                request.Headers.Add("Accept", "application/json;v=2.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                var result = new List<Guid>();

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return JsonConvert.DeserializeObject<OSAScanDto[]>(response.Content.ReadAsStringAsync().Result);
                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }
        }


        /// <summary>
        /// Gets the osa results.
        /// </summary>
        /// <param name="osaScanId">The osa scan identifier.</param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException">
        /// </exception>
        /*
         * {
  "totalLibraries": 37,
  "highVulnerabilityLibraries": 6,
  "mediumVulnerabilityLibraries": 1,
  "lowVulnerabilityLibraries": 0,
  "nonVulnerableLibraries": 30,
  "vulnerableAndUpdated": 0,
  "vulnerableAndOutdated": 7,
  "vulnerabilityScore": "High",
  "totalHighVulnerabilities": 25,
  "totalMediumVulnerabilities": 12,
  "totalLowVulnerabilities": 1 
         */
        public OSAReportDto GetOSAResults(Guid osaScanId)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"osa/reports?scanId={osaScanId}"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var result = JsonConvert.DeserializeObject<OSAReportDto>(response.Content.ReadAsStringAsync().Result);

                    var licenses = GetOSALicenses(osaScanId).ToDictionary(x => x.Id);

                    var libraries = GetOSALibraries(osaScanId);

                    foreach (var library in libraries)
                    {
                        foreach (var item in library.Licenses)
                        {
                            if (licenses.ContainsKey(item))
                            {
                                string riskLevel = licenses[item].RiskLevel;
                                if (riskLevel == "High" || riskLevel == "Medium")
                                {
                                    result.LibrariesAtLegalRick++;
                                }
                            }
                        }

                        if (library.Outdated)
                            result.TotalNumberOutdatedVersionLibraries++;
                    }

                    return result;
                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }

            throw new NotSupportedException();
        }

        public IList<OSALibraryDto> GetOSALibraries(Guid osaScanId)
        {
            if (!Connected)
                throw new NotSupportedException();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"osa/libraries?scanId={osaScanId}&itemsPerPage=20000"))
            {
                request.Headers.Add("Accept", "application/json;v=2.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var result = JsonConvert.DeserializeObject<List<OSALibraryDto>>(response.Content.ReadAsStringAsync().Result);

                    return result;
                }
            }

            throw new NotSupportedException();
        }

        public IList<OSALicenseDto> GetOSALicenses(Guid osaScanId)
        {
            if (!Connected)
                throw new NotSupportedException();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"osa/licenses?scanId={osaScanId}"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var result = JsonConvert.DeserializeObject<List<OSALicenseDto>>(response.Content.ReadAsStringAsync().Result);

                    return result;
                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }
        }

        #endregion

        #region SAST

        public void ReScan(long projectId, byte[] zipdata, string comment = "")
        {
            checkConnection();

            using (var content = new MultipartFormDataContent())
            {
                var fileContent = new ByteArrayContent(zipdata);
                fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileName = "filename.zip",
                    Name = "zippedSource"
                };
                content.Add(fileContent);

                string requestUri = $"projects/{projectId}/sourceCode/attachments";

                var result = httpClient.PostAsync(requestUri, content).Result;
            }

            ForceRunScan(projectId, comment);
        }

        public void ForceRunScan(long projectId, string comment = "")
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, "sast/scans"))
            {
                request.Headers.Add("Accept", "application/json;v=1.1");
                request.Headers.Add("cxOrigin", "Log4Shell - Scan Scheduler");

                var requestBody = JsonConvert.SerializeObject(new ScanDetails
                {
                    ProjectId = projectId,
                    Comment = comment ?? string.Empty,
                    ForceScan = true,
                    IsIncremental = false,
                    IsPublic = true
                });

                using (var stringContent = new StringContent(requestBody, Encoding.UTF8, "application/json"))
                {
                    request.Content = stringContent;
                    HttpResponseMessage response = httpClient.SendAsync(request).Result;

                    if (response.StatusCode != HttpStatusCode.Created 
                     && response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
                    }
                }
            }
        }

        public void RunSASTScan(long projectId, string comment = "")
        {
            checkConnection();

            var projectResponse = _cxPortalWebServiceSoapClient.GetProjectConfiguration(_soapSessionId, projectId);

            if (!projectResponse.IsSuccesfull)
                throw new Exception(projectResponse.ErrorMessage);

            if (projectResponse.ProjectConfig.SourceCodeSettings.SourceOrigin == PortalSoap.SourceLocationType.Local)
                throw new NotSupportedException("The location is setup for Local, we don't support it");

            ForceRunScan(projectId, comment);
        }

        public SASTResults GetSASTResults(long sastScanId)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"sast/scans/{sastScanId}/resultsStatistics"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var sastRes = JsonConvert.DeserializeObject<SASTResults>(response.Content.ReadAsStringAsync().Result);
                    sastRes.Id = sastScanId;
                    return sastRes;
                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }
        }

        /*
         * 
         * sast/scans?last={numberOfScans}&scanStatus={state}&projectId={projectId} 
         * 
         */
        public IEnumerable<int> GetSASTScans(int projectId, string scanState = "Finished", int? numberOfScans = 1)
        {
            var l = GetSASTAllScans(projectId, scanState, numberOfScans);
            return l.Select(x => int.Parse((string)x.SelectToken("id")));
        }

        public JArray GetSASTAllScans(int projectId, string scanState = null, int? numberOfScans = null)
        {
            checkConnection();

            string sastFilter = $"projectId={projectId}";

            if (numberOfScans != null)
                sastFilter += $"&last={numberOfScans}";

            if (!string.IsNullOrWhiteSpace(scanState))
                sastFilter += $"&scanStatus={scanState}";

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"sast/scans?{sastFilter}"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var scans = (JArray)JsonConvert.DeserializeObject(response.Content.ReadAsStringAsync().Result);
                    return scans;
                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }
        }

        /// <summary>
        /// sast/scans
        /// </summary>
        /// <param name="projectId"></param>
        /// <param name="scanState"></param>
        /// <param name="numberOfScans"></param>
        /// <returns></returns>
        public ICollection<Scan> GetSASTScanSummary(int projectId, string scanState = null, int? numberOfScans = null)
        {
            checkConnection();

            string sastFilter = $"projectId={projectId}";

            if (numberOfScans != null)
                sastFilter += $"&last={numberOfScans}";

            if (!string.IsNullOrWhiteSpace(scanState))
                sastFilter += $"&scanStatus={scanState}";

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"sast/scans?{sastFilter}"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var objRes = response.Content.ReadAsStringAsync().Result;
                    return JsonConvert.DeserializeObject<List<Scan>>(objRes);
                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }
        }

        private enum ScanRetrieveKind
        {
            First,
            Last,
            Locked,
            All
        }

        public IEnumerable<Scan> GetAllSASTScans(long projectId)
        {
            var scans = GetScans(projectId, true, ScanRetrieveKind.All);
            return scans;
        }
        public Scan GetFirstScan(long projectId)
        {
            var scan = GetScans(projectId, true, ScanRetrieveKind.First);
            return scan.FirstOrDefault();
        }

        public Scan GetLastScan(long projectId)
        {
            var scan = GetScans(projectId, true, ScanRetrieveKind.Last);
            return scan.FirstOrDefault();
        }

        public Scan GetLockedScan(long projectId)
        {
            return GetScans(projectId, true, ScanRetrieveKind.First).FirstOrDefault();
        }

        public int GetScanCount()
        {
            checkConnection();
            return _oDataScans.Count();
        }

        public List<QueuedScan> GetScanQueue()
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"sast/scansQueue"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var objRes = response.Content.ReadAsStringAsync().Result;
                    return JsonConvert.DeserializeObject<List<QueuedScan>>(objRes);
                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }

        }

        public class ProjectLastScan
        {
            public long ProjectId { get; set; }
            public long ScanId { get; set; }
        }

        public IEnumerable<ProjectLastScan> GetScansForLanguageNotScannedSince(string language, bool finished, DateTime notScannedSince)
        {
            checkConnection();

            var projects = _oDataProjects
                .Where(x => x.LastScan != null)
                .Select(x => new 
            { 
                x.Id, x.LastScanId, x.LastScan.ScanCompletedOn, x.LastScan.ScannedLanguages, x.LastScan.ScanType
            })
                .ToList()
                .Where(x => x.ScanCompletedOn <= notScannedSince)
                .Where(x => x.ScannedLanguages.Where(y => y.LanguageName == language).Any())
                .Where(x => x.ScanType != 3);

            return projects.Select(x => new ProjectLastScan { ProjectId = x.Id, ScanId = x.LastScanId.Value });
        }

        private IEnumerable<Scan> GetScans(long projectId, bool finished,
            ScanRetrieveKind scanKind = ScanRetrieveKind.All)
        {
            checkConnection();

            IQueryable<CxDataRepository.Scan> scans = _oDataScans.Where(x => x.ProjectId == projectId);

            switch (scanKind)
            {
                case ScanRetrieveKind.First:
                    scans = scans.Take(1);
                    break;
                case ScanRetrieveKind.Last:
                    scans = scans.Skip(Math.Max(0, scans.Count() - 1));
                    break;

                case ScanRetrieveKind.Locked:
                    scans = scans.Where(x => x.IsLocked);
                    break;
                case ScanRetrieveKind.All:
                    break;
            }

            foreach (var scan in scans)
            {
                if (finished && scan.ScanType == 3)
                    continue;

                yield return ConvertScanFromOData(scan);
            }
        }

        private static Scan ConvertScanFromOData(CxDataRepository.Scan scan)
        {
            return new Scan
            {
                Comment = scan.Comment,
                Id = scan.Id,
                IsLocked = scan.IsLocked,
                InitiatorName = scan.InitiatorName,
                OwningTeamId = scan.OwningTeamId,
                ProjectId = scan.ProjectId,
                ScanState = new ScanState
                {
                    LanguageStateCollection = scan.ScannedLanguages.Select(language => new LanguageStateCollection
                    {
                        LanguageName = language.LanguageName
                    }).ToList(),

                    LinesOfCode = scan.LOC.GetValueOrDefault(),
                    CxVersion = scan.ProductVersion,
                },
                Origin = scan.Origin,
                ScanRisk = scan.RiskScore,
                DateAndTime = new DateAndTime
                {
                    EngineFinishedOn = scan.EngineFinishedOn,
                    EngineStartedOn = scan.EngineStartedOn
                },
                Results = new SASTResults
                {
                    High = (uint)scan.High,
                    Medium = (uint)scan.Medium,
                    Low = (uint)scan.Low,
                    FailedLoC = (int)scan.FailedLOC.GetValueOrDefault(),
                    LoC = (int)scan.LOC.GetValueOrDefault()
                }
            };
        }

        public Scan GetScanById(long scanId)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"sast/scans/{scanId}"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var objRes = response.Content.ReadAsStringAsync().Result;
                    return JsonConvert.DeserializeObject<Scan>(objRes);
                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }
        }

        // get preset /sast/scanSettings/{projectId}
        public string GetSASTPreset(int projectId)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"sast/scanSettings/{projectId}"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var result = JsonConvert.DeserializeObject<ScanSettings>(response.Content.ReadAsStringAsync().Result);

                    if (!GetPresets().ContainsKey(result.Preset.Id))
                        return "Checkmarx Default";

                    return GetPresets()[result.Preset.Id];
                }

                throw new NotSupportedException(response.ToString());
            }
        }

        #endregion

        #region Checkmarx Configurations

        public List<ComponentConfiguration> GetConfigurations(SAST.Group group)
        {
            checkConnection();

            using (var request = new HttpRequestMessage(HttpMethod.Get, $"configurationsExtended/{ConvertToString(group)}"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return JsonConvert.DeserializeObject<List<ComponentConfiguration>>(response.Content.ReadAsStringAsync().Result);
                }

                throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
            }
        }

        #endregion

        /// <summary>
        /// Gets the projects.
        /// Id - Name
        /// </summary>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Dictionary<int, string> GetProjects()
        {
            checkConnection();

            // List Project Now..
            var projectListResponse = httpClient.GetAsync("projects").Result;

            var result = new Dictionary<int, string>();

            if (projectListResponse.StatusCode == HttpStatusCode.OK)
            {
                string plResult = projectListResponse.Content.ReadAsStringAsync().Result;

                var projectList = (JArray)JsonConvert.DeserializeObject(plResult);

                foreach (var item in projectList)
                {
                    result.Add((int)item.SelectToken("id"),
                        (string)item.SelectToken("name"));
                }

                return result;
            }

            if (projectListResponse.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException();

            throw new NotSupportedException(projectListResponse.Content.ReadAsStringAsync().Result);
        }

        public List<ProjectDetails> GetAllProjectsDetails()
        {
            checkConnection();

            // List Project Now..
            var projectListResponse = httpClient.GetAsync("projects").Result;

            if (projectListResponse.StatusCode == HttpStatusCode.OK)
            {
                string plResult = projectListResponse.Content.ReadAsStringAsync().Result;
                return JsonConvert.DeserializeObject<List<ProjectDetails>>(plResult);
            }

            if (projectListResponse.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException();

            throw new NotSupportedException(projectListResponse.Content.ReadAsStringAsync().Result);
        }

        public T GetRequest<T>(string requestUri)
        {
            checkConnection();

            try
            {
                var uri = new Uri(SASTServerURL, requestUri);
                if (!_isV9 && uri.LocalPath.ToLowerInvariant().StartsWith("/cxwebinterface"))
                {
                    var byteArray = Encoding.ASCII.GetBytes($"{Username}:{Password}");
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
                }

                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    HttpResponseMessage response = httpClient.SendAsync(request).Result;

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return JsonConvert.DeserializeObject<T>(response.Content.ReadAsStringAsync().Result);
                    }

                    throw new NotSupportedException(response.Content.ReadAsStringAsync().Result);
                }
            }
            finally
            {
                httpClient.DefaultRequestHeaders.Authorization = AuthenticationToken;
            }
        }

        private Dictionary<int, string> _presetsCache;
        public Dictionary<int, string> GetPresets()
        {
            checkConnection();

            if (_presetsCache != null)
                return _presetsCache;

            using (var presets = new HttpRequestMessage(HttpMethod.Get, $"sast/presets"))
            {
                presets.Headers.Add("Accept", "application/json;v=1.0");

                HttpResponseMessage presetResponse = httpClient.SendAsync(presets).Result;

                if (presetResponse.StatusCode == HttpStatusCode.OK)
                {
                    var presetArray = JsonConvert.DeserializeObject<JArray>(presetResponse.Content.ReadAsStringAsync().Result);

                    _presetsCache = presetArray.ToDictionary(x => (int)x.SelectToken("id"), x => (string)x.SelectToken("name"));

                    return _presetsCache;
                }

                throw new NotSupportedException(presetResponse.ToString());
            }
        }

        #region Reports

        /// <summary>
        /// Returns the ScanId of a finished scan.
        /// </summary>
        /// <param name="scanId"></param>
        /// <returns></returns>
        public byte[] GetScanLogs(long scanId)
        {
            checkConnection();

            if (scanId <= 0)
                throw new ArgumentOutOfRangeException(nameof(scanId));


            var objectName = new CxWSRequestScanLogFinishedScan
            {
                SessionID = _soapSessionId,
                ScanId = scanId
            };

            System.Xml.Serialization.XmlSerializer x = new System.Xml.Serialization.XmlSerializer(objectName.GetType());

            StringBuilder sb = new StringBuilder();
            using (StringWriter xmlWriter = new StringWriter(sb))
            {
                x.Serialize(xmlWriter, objectName);
            }

            Console.WriteLine(sb.ToString());

            var result = _cxPortalWebServiceSoapClient.GetScanLogs(objectName);



            if (!result.IsSuccesfull)
                throw new ActionNotSupportedException(result.ErrorMessage);

            return result.ScanLog;
        }

        /// <summary>
        /// Returns a stream of the scan report.
        /// </summary>
        /// <param name="scanId"></param>
        /// <param name="reportType"></param>
        /// <returns></returns>
        public Stream GetScanReport(long scanId, ReportType reportType)
        {
            checkConnection();

            // reportType = "XML"
            // scanId = scanId

            // POST reports/sastScan
            string reportTypeString = GetReportTypeString(reportType);

            using (var request = new HttpRequestMessage(HttpMethod.Post, "reports/sastScan"))
            {
                request.Headers.Add("Accept", "application/json;v=1.0");

                var values = new List<KeyValuePair<string, string>> {
                    new KeyValuePair<string, string>("reportType", reportTypeString ),
                    new KeyValuePair<string, string>("scanId", scanId.ToString()),
                };

                request.Content = new FormUrlEncodedContent(values);

                HttpResponseMessage response = httpClient.SendAsync(request).Result;

                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    JObject createReport = JsonConvert.DeserializeObject<JObject>(response.Content.ReadAsStringAsync().Result);

                    long reportId = (long)createReport["reportId"];

                    while (!IsReportReady(reportId))
                    {
                        // wait 
                        Thread.Sleep(TimeSpan.FromSeconds(1));
                    }

                    // Get Report
                    using (var getReport = new HttpRequestMessage(HttpMethod.Get, $"reports/sastScan/{reportId}"))
                    {
                        getReport.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue($"application/{reportTypeString}"));

                        HttpResponseMessage getReportResponse = httpClient.SendAsync(getReport).Result;

                        if (getReportResponse.StatusCode == HttpStatusCode.OK)
                        {
                            //string fileName = $"{scanId} - {reportId}.{reportType}";
                            //while (File.Exists(fileName))
                            //    File.Delete(fileName);
                            return getReportResponse.Content.ReadAsStreamAsync().Result;
                        }
                    }
                }
            }

            throw new NotSupportedException();
        }

        /// <summary>
        /// Get the report and saves it inside a file in the FileSystem.
        /// </summary>
        /// <param name="scanId"></param>
        /// <param name="reportType"></param>
        /// <param name="fullOutputFileName"></param>
        /// <returns></returns>
        public string GetScanReportFile(long scanId, ReportType reportType, string fullOutputFileName)
        {
            if (string.IsNullOrWhiteSpace(fullOutputFileName))
                throw new NotSupportedException(nameof(fullOutputFileName));

            var memoryStream = GetScanReport(scanId, reportType);
            using (FileStream fs = File.Create(fullOutputFileName))
            {
                memoryStream.CopyTo(fs);
            }

            return Path.GetFullPath(fullOutputFileName);
        }

        /// <summary>
        /// Get the XML document for the reports.
        /// </summary>
        /// <param name="scanId">Id of the scan</param>
        /// <returns></returns>
        public XDocument GetScanReport(long scanId)
        {
            return XDocument.Load(GetScanReport(scanId, ReportType.XML));
        }

        private static string GetReportTypeString(ReportType reportType)
        {
            switch (reportType)
            {
                case ReportType.XML:
                    return "XML";
                case ReportType.PDF:
                    return "PDF";
                case ReportType.CSV:
                    return "CSV";
                case ReportType.RTF:
                    return "RTF";
                default:
                    throw new NotSupportedException();
            }
        }

        public CxWSResponseScansDisplayData GetScansDisplayData(long projectId)
        {
            if (!Connected) throw new NotSupportedException();
            var res = _cxPortalWebServiceSoapClient.GetScansDisplayData(_soapSessionId, projectId);
            if (res.IsSuccesfull)
            {
                return res;
            }
            throw new Exception(res.ErrorMessage);
        }

        private bool IsReportReady(long reportId)
        {
            if (!Connected)
                throw new NotSupportedException();

            using (var getReportStatus = new HttpRequestMessage(HttpMethod.Get, $"reports/sastScan/{reportId}/status"))
            {
                HttpResponseMessage getReportResponse = httpClient.SendAsync(getReportStatus).Result;

                if (getReportResponse.StatusCode == HttpStatusCode.OK)
                {
                    JObject reportStatus = JsonConvert.DeserializeObject<JObject>(getReportResponse.Content.ReadAsStringAsync().Result);

                    return (string)reportStatus.SelectToken("status").SelectToken("value") == "Created";
                }
            }

            return false;
        }

        #endregion

        #region Results

        public CxWSSingleResultData[] GetResultsForScan(long scanId)
        {
            checkConnection();

            var result = _cxPortalWebServiceSoapClient.GetResultsForScan(_soapSessionId, scanId);

            if (!result.IsSuccesfull)
                throw new ApplicationException(result.ErrorMessage);

            return result.Results;
        }

        /// <summary>
        /// Get the all query groups of the CxSAST server.
        /// </summary>
        /// <returns></returns>
        public cxPortalWebService93.CxWSQueryGroup[] GetQueries()
        {
            checkConnection();

            var result = _cxPortalWebServiceSoapClientV9.GetQueryCollectionAsync(_soapSessionId).Result;

            if (!result.IsSuccesfull)
                throw new ApplicationException(result.ErrorMessage);

            return result.QueryGroups;

        }

        /// <summary>
        /// Comment Separator.
        /// </summary>
        public static char CommentSeparator
        {
            get
            {
                return Convert.ToChar(255);
            }
        }


        public void GetCommentsHistoryTest(long scanId)
        {
            checkConnection();

            var response = _cxPortalWebServiceSoapClient.GetResultsForScan(_soapSessionId, scanId);

            // Assert.IsTrue(response.IsSuccesfull);

            foreach (var item in response.Results)
            {
                //Console.WriteLine(item.State);

                if (!string.IsNullOrWhiteSpace(item.Comment))
                {
                    var commentsResults = _cxPortalWebServiceSoapClient.GetPathCommentsHistory(_soapSessionId, scanId, item.PathId,
                         PortalSoap.ResultLabelTypeEnum.Remark);

                    foreach (var comment in commentsResults.Path.Comment.Split(new[] { CommentSeparator },
                        StringSplitOptions.RemoveEmptyEntries))
                    {
                        string commentText = comment.Substring(comment.IndexOf(']') + 3);

                        Console.WriteLine(string.Join(";",
                            toSeverityToString(item.Severity), toResultStateToString((ResultState)item.State), commentText));
                    }
                }
            }
        }

        public enum ResultState : int
        {
            ToVerify = 0,
            NonExploitable = 1,
            Confirmed = 2,
            Urgent = 3,
            ProposedNotExploitable = 4
        }


        /// <summary>
        /// Get the HTML Query Description (?)
        /// </summary>
        /// <param name="cwe">CWE Id</param>
        /// <returns>For 0 returns empty, for less than 0 throws an exception, for the other an HTML Query Description</returns>
        public string GetCWEDescription(long cwe)
        {
            if (cwe < 0)
                throw new ArgumentOutOfRangeException(nameof(cwe));

            if (cwe == 0)
                return string.Empty;

            checkConnection();

            var result = _cxPortalWebServiceSoapClient.GetQueryDescription(_soapSessionId, (int)cwe);
            if (!result.IsSuccesfull)
                throw new Exception(result.ErrorMessage);

            return result.QueryDescription;
        }


        public static string toResultStateToString(ResultState state)
        {
            switch (state)
            {
                case ResultState.ToVerify:
                    return "To Verify";
                case ResultState.NonExploitable:
                    return "Non Exploitable";
                case ResultState.Confirmed:
                    return "Confirmed";
                case ResultState.Urgent:
                    return "Urgent";
                case ResultState.ProposedNotExploitable:
                    return "Proposed Not Exploitable";
                default:
                    throw new NotImplementedException();
            }
        }

        public static string toSeverityToString(int severity)
        {
            switch (severity)
            {
                case 0:
                    return "Low";
                case 1:
                    return "Low";
                case 2:
                    return "Medium";
                case 3:
                    return "High";
                default:
                    throw new NotImplementedException();
            }
        }

        #endregion


        #region Utils

        private string ConvertToString(object value, CultureInfo cultureInfo = null)
        {
            if (value == null)
            {
                return "";
            }

            if (cultureInfo == null)
                cultureInfo = CultureInfo.InvariantCulture;

            if (value is System.Enum)
            {
                var name = System.Enum.GetName(value.GetType(), value);
                if (name != null)
                {
                    var field = System.Reflection.IntrospectionExtensions.GetTypeInfo(value.GetType()).GetDeclaredField(name);
                    if (field != null)
                    {
                        var attribute = System.Reflection.CustomAttributeExtensions.GetCustomAttribute(field, typeof(System.Runtime.Serialization.EnumMemberAttribute))
                            as System.Runtime.Serialization.EnumMemberAttribute;
                        if (attribute != null)
                        {
                            return attribute.Value != null ? attribute.Value : name;
                        }
                    }

                    var converted = System.Convert.ToString(System.Convert.ChangeType(value, System.Enum.GetUnderlyingType(value.GetType()), cultureInfo));
                    return converted == null ? string.Empty : converted;
                }
            }
            else if (value is bool)
            {
                return System.Convert.ToString((bool)value, cultureInfo).ToLowerInvariant();
            }
            else if (value is byte[])
            {
                return System.Convert.ToBase64String((byte[])value);
            }
            else if (value.GetType().IsArray)
            {
                var array = System.Linq.Enumerable.OfType<object>((System.Array)value);
                return string.Join(",", System.Linq.Enumerable.Select(array, o => ConvertToString(o, cultureInfo)));
            }

            var result = System.Convert.ToString(value, cultureInfo);
            return result == null ? "" : result;
        }

        #endregion


        public void Dispose()
        {
            // There is logoff...

        }


    }


}
