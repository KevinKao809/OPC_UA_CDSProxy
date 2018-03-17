using Newtonsoft.Json.Linq;
using Opc.Ua;
using Opc.Ua.Client;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace CDSProxy.Helper
{
    public class UAClientHelper
    {
        string _endpointURL = "", _userId = "", _userPassword = "";
        bool _autoAccept = true;
        Session _uaSession = null;
        private static string _CDSConfigurationFilename = $"{System.IO.Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}CDSConfiguration.json";

        public UAClientHelper()
        {
            try
            {
                if (File.Exists(_CDSConfigurationFilename))
                {
                    dynamic Entries = JObject.Parse(File.ReadAllText(_CDSConfigurationFilename));                    
                    _endpointURL = Entries.OPCUAEndpointURL;
                    if (Entries.OPCUAUserIdentity != null)
                    { 
                        _userId = Entries.OPCUAUserIdentity.id;
                        _userPassword = Entries.OPCUAUserIdentity.password;
                    }
                    _autoAccept = Entries.OPCUAAutoAccept;
                }
                else
                {
                    throw new Exception("Can't load File:CDSConfiguration.json");
                }
            }
            catch (Exception)
            {
                throw;
            }            
        }

        public UAClientHelper(string endPointURL, string userId, string userPassword, bool autoAcceptCert)
        {            
            _endpointURL = endPointURL;
            _userId = userId;
            _userPassword = userPassword;
            _autoAccept = autoAcceptCert;
        }

        public async Task<bool> Connect()
        {
            try
            {
                var config = new ApplicationConfiguration()
                {
                    ApplicationName = "CDSProxy",
                    ApplicationType = ApplicationType.Client,
                    ApplicationUri = "urn:" + Utils.GetHostName() + ":OPCFoundation:CDSProxy",
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier
                        {
                            StoreType = "X509Store",
                            StorePath = "CurrentUser\\My",
                            SubjectName = "CDSProxy"
                        },
                        TrustedPeerCertificates = new CertificateTrustList
                        {
                            StoreType = "Directory",
                            StorePath = "OPC Foundation/CertificateStores/UA Applications",
                        },
                        TrustedIssuerCertificates = new CertificateTrustList
                        {
                            StoreType = "Directory",
                            StorePath = "OPC Foundation/CertificateStores/UA Certificate Authorities",
                        },
                        RejectedCertificateStore = new CertificateTrustList
                        {
                            StoreType = "Directory",
                            StorePath = "OPC Foundation/CertificateStores/RejectedCertificates",
                        },
                        NonceLength = 32,
                        AutoAcceptUntrustedCertificates = _autoAccept,

                        //Update by Kevin Kao
                        RejectSHA1SignedCertificates = false,
                        MinimumCertificateKeySize = 1,
                    },
                    TransportConfigurations = new TransportConfigurationCollection(),
                    TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                    ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
                };

                await config.Validate(ApplicationType.Client);

                bool haveAppCertificate = config.SecurityConfiguration.ApplicationCertificate.Certificate != null;

                if (!haveAppCertificate)
                {
                    Console.WriteLine("    INFO: Creating new application certificate: {0}", config.ApplicationName);

                    X509Certificate2 certificate = CertificateFactory.CreateCertificate(
                        config.SecurityConfiguration.ApplicationCertificate.StoreType,
                        config.SecurityConfiguration.ApplicationCertificate.StorePath,
                        null,
                        config.ApplicationUri,
                        config.ApplicationName,
                        config.SecurityConfiguration.ApplicationCertificate.SubjectName,
                        null,
                        CertificateFactory.defaultKeySize,
                        DateTime.UtcNow - TimeSpan.FromDays(1),
                        CertificateFactory.defaultLifeTime,
                        CertificateFactory.defaultHashSize,
                        false,
                        null,
                        null
                        );

                    config.SecurityConfiguration.ApplicationCertificate.Certificate = certificate;

                }

                haveAppCertificate = config.SecurityConfiguration.ApplicationCertificate.Certificate != null;

                if (haveAppCertificate)
                {
                    config.ApplicationUri = Utils.GetApplicationUriFromCertificate(config.SecurityConfiguration.ApplicationCertificate.Certificate);

                    if (config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
                    {
                        config.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(CertificateValidator_CertificateValidation);
                    }
                }
                else
                {
                    Console.WriteLine("    WARN: missing application certificate, using unsecure connection.");
                }

                Console.WriteLine("Discover endpoints of {0}.", _endpointURL);
                var selectedEndpoint = CoreClientUtils.SelectEndpoint(_endpointURL, haveAppCertificate, 15000);
                Console.WriteLine("Selected endpoint uses: {0}",
                    selectedEndpoint.SecurityPolicyUri.Substring(selectedEndpoint.SecurityPolicyUri.LastIndexOf('#') + 1));

                Console.WriteLine("Create a session with OPC UA server.");
                var endpointConfiguration = EndpointConfiguration.Create(config);
                var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);

                UserIdentity userIdentity = null;
                switch (endpoint.SelectedUserTokenPolicy.TokenType)
                {
                    case UserTokenType.UserName:
                        userIdentity = new UserIdentity(_userId, _userPassword);
                        break;
                    case UserTokenType.Anonymous:
                        userIdentity = new UserIdentity(new AnonymousIdentityToken());
                        break;
                }                
                this._uaSession = await Session.Create(config, endpoint, false, "CDSProxy", 60000, userIdentity, null);                
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception: " + ex.Message);
                return false;
            }
            return true;
        }

        public DataValueCollection Read(ReadValueIdCollection nodesToRead)
        {
            if (nodesToRead == null || nodesToRead.Count == 0)
            {
                return null;
            }

            DataValueCollection values = null;
            DiagnosticInfoCollection diagnosticInfos = null;

            ResponseHeader responseHeader = _uaSession.Read(
                null,
                0,
                TimestampsToReturn.Both,
                nodesToRead,
                out values,
                out diagnosticInfos);

            ClientBase.ValidateResponse(values, nodesToRead);
            ClientBase.ValidateDiagnosticInfos(diagnosticInfos, nodesToRead);

            return values;
        }

        public StatusCodeCollection Write(WriteValueCollection nodesToWrite)
        {
            if (nodesToWrite == null || nodesToWrite.Count == 0)
            {
                return null;
            }

            foreach (WriteValue nodeToWrite in nodesToWrite)
            {
                NumericRange indexRange;
                ServiceResult result = NumericRange.Validate(nodeToWrite.IndexRange, out indexRange);

                if (ServiceResult.IsGood(result) && indexRange != NumericRange.Empty)
                {
                    // apply the index range.
                    object valueToWrite = nodeToWrite.Value.Value;

                    result = indexRange.ApplyRange(ref valueToWrite);

                    if (ServiceResult.IsGood(result))
                    {
                        nodeToWrite.Value.Value = valueToWrite;
                    }
                }
            }

            StatusCodeCollection results = null;
            DiagnosticInfoCollection diagnosticInfos = null;

            ResponseHeader responseHeader = _uaSession.Write(
                null,
                nodesToWrite,
                out results,
                out diagnosticInfos);

            ClientBase.ValidateResponse(results, nodesToWrite);
            ClientBase.ValidateDiagnosticInfos(diagnosticInfos, nodesToWrite);

            return results;
        }

        private static void CertificateValidator_CertificateValidation(CertificateValidator validator, CertificateValidationEventArgs e)
        {
            Console.WriteLine("Accepted Certificate: {0}", e.Certificate.Subject);
            e.Accept = (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted);
        }
    }
}
