using Microsoft.Azure.Devices.Client;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;

namespace CDSProxy.Helper
{
    public class CDSHelper
    {
        string _APIURL, _IoTDeviceID, _IoTDevicePW, _AccessToken, _IoTHubName, _IoTHubProtocol;
        string _IoTHubAuthenticationType, _IoTDeviceKey, _ContainerName, _CertificateFileName, _CertificatePassword;

        public DeviceClient deviceClient;
        private static string _CDSConfigurationFilename = $"{System.IO.Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}CDSConfiguration.json";

        public CDSHelper()
        {
            try
            {
                if (File.Exists(_CDSConfigurationFilename))
                {
                    dynamic Entries = JObject.Parse(File.ReadAllText(_CDSConfigurationFilename));
                    string apiURL = Entries.APIURL;
                    if (!apiURL.StartsWith("http"))
                        throw new Exception("invalid URL");
                    if (!apiURL.EndsWith("/"))
                        apiURL = apiURL + "/";

                    _APIURL = apiURL;
                    _IoTDeviceID = Entries.IoTDeviceID;
                    _IoTDevicePW = Entries.IoTDevicePW;
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
            ServicePointManager.ServerCertificateValidationCallback += new RemoteCertificateValidationCallback((sender, certificate, chain, policyErrors) => { return true; });
        }
        public CDSHelper(string apiURL, string iotDeviceID, string iotDevicePassword)
        {
            if (!apiURL.StartsWith("http"))
                throw new Exception("invalid URL");
            if (!apiURL.EndsWith("/"))
                apiURL = apiURL + "/";

            _APIURL = apiURL;
            _IoTDeviceID = iotDeviceID;
            _IoTDevicePW = iotDevicePassword;
            ServicePointManager.ServerCertificateValidationCallback += new RemoteCertificateValidationCallback((sender, certificate, chain, policyErrors) => { return true; });
        }

        public async Task<bool> Connect()
        {
            string endPointURI = _APIURL + "device-api/Device/" + _IoTDeviceID;
            try
            {
                string IoTDeviceString = await callAPIService("GET", endPointURI, null);
                dynamic IoTDeviceJSONObj = JObject.Parse(IoTDeviceString);
                _IoTHubName = IoTDeviceJSONObj.IoTHubName;
                _IoTHubProtocol = IoTDeviceJSONObj.IoTHubProtocol;
                _IoTHubAuthenticationType = IoTDeviceJSONObj.IoTHubAuthenticationType;
                _IoTDeviceKey = IoTDeviceJSONObj.DeviceKey;
                _ContainerName = IoTDeviceJSONObj.ContainerName;
                _CertificateFileName = IoTDeviceJSONObj.CertificateFileName;
                _CertificatePassword = IoTDeviceJSONObj.CertificatePassword;
            
                TransportType protocol = TransportType.Http1;
                switch (_IoTHubProtocol.ToLower())
                {
                    case "amqp":
                        protocol = TransportType.Amqp;
                        break;
                    case "mqtt":
                        protocol = TransportType.Mqtt;
                        break;
                    case "https":
                        protocol = TransportType.Http1;
                        break;
                }
                deviceClient = DeviceClient.Create(_IoTHubName, new DeviceAuthenticationWithRegistrySymmetricKey(_IoTDeviceID, _IoTDeviceKey), protocol);
                Console.WriteLine("IoT Hub Connected: " + _IoTHubName);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception on IoT Hub Connect, Error:" + ex.Message);
                return false;
            }
            return true;
        }

        private async Task<string> callAPIService(string method, string endPointURI, string postData)
        {
            HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(endPointURI);
            request.Method = method;
            HttpWebResponse response = null;

            try
            {
                request.ContentType = "application/x-www-form-urlencoded";
                request.Headers.Add("Authorization", "Bearer " + _AccessToken);

                switch (method.ToLower())
                {
                    case "get":
                    case "delete":
                        response = request.GetResponse() as HttpWebResponse;
                        break;
                    case "post":
                    case "put":
                        using (Stream requestStream = request.GetRequestStream())
                        using (StreamWriter writer = new StreamWriter(requestStream, Encoding.ASCII))
                        {
                            writer.Write(postData);
                        }
                        response = (HttpWebResponse)request.GetResponse();
                        break;
                    default:
                        throw new Exception("Method:" + method + " Not Support");
                }
                using (StreamReader sr = new StreamReader(response.GetResponseStream()))
                {
                    return sr.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var httpResponse = (HttpWebResponse)ex.Response;

                if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (getAPIToken())
                        return await callAPIService(method, endPointURI, postData);
                }
                else
                    throw new Exception(response.StatusCode.ToString());
            }
            catch (Exception)
            {
                throw;
            }

            return null;
        }
        private bool getAPIToken()
        {
            string uri = _APIURL + "token";
            HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(uri);
            request.Method = "post";
            HttpWebResponse response = null;
            string tokenRole = "device";
            string postData = "grant_type=password&email=" + _IoTDeviceID + "&password=" + _IoTDevicePW + "&role=" + tokenRole;

            using (Stream requestStream = request.GetRequestStream())
            using (StreamWriter writer = new StreamWriter(requestStream, Encoding.ASCII))
            {
                writer.Write(postData);
            }
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception: " + ex.Message);
                return false;
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                string result;
                using (StreamReader sr = new StreamReader(response.GetResponseStream()))
                {
                    result = sr.ReadToEnd();
                }
                dynamic access_result = JObject.Parse(result);
                _AccessToken = access_result.access_token;
                return true;
            }
            else
            {
                if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new Exception("Authentication Fail");
                else
                    throw new Exception();
            }
        }
    }
}
