using CDSProxy.Helper;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Client.Exceptions;
using Newtonsoft.Json.Linq;
using Opc.Ua;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CDSProxy
{
    class action
    {
        public string nodeId;
        public BuiltInType dataType = BuiltInType.Null;
        public string dataValue;
    }

    class CommandActions
    {
        public string command;
        public List<action> actions;
    }

    public class MessageCommand
    {
        private const int UACommandInterval = 20;
        private List<CommandActions> _initialCommandList = null;
        private List<CommandActions> _MessageCommandList = null;
        private static string _CDSConfigurationFilename = $"{System.IO.Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}CDSConfiguration.json";
        UAClientHelper _uaClient = null;
        CDSHelper _cdsClient = null;

        public MessageCommand()
        {
            try
            {
                // Load OPC UA Server Setting
                _uaClient = new UAClientHelper();

                // Load CDS/IoT Hub Setting
                _cdsClient = new CDSHelper();

                // Load Initial and Message Command
                loadCommandConfiguration();
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<bool> Start()
        {
            // Connect to OPC UA Server
            if (await _uaClient.Connect())
            {
                retriveNodeIdDataType(ref _initialCommandList);
                retriveNodeIdDataType(ref _MessageCommandList);

                // Connect to CDS/IoT Hub
                if (await _cdsClient.Connect())
                {
                    OpcUAServerInitialCommand();

                    Console.WriteLine("\nReceiving cloud to device messages from service");
                    while (true)
                    {
                        try
                        {
                            Message receivedMessage = await _cdsClient.deviceClient.ReceiveAsync();
                            if (receivedMessage == null) continue;

                            Console.ForegroundColor = ConsoleColor.Yellow;
                            string messageContent = Encoding.ASCII.GetString(receivedMessage.GetBytes());
                            Console.WriteLine("Received message: {0}", messageContent);
                            Console.ResetColor();
                            await _cdsClient.deviceClient.CompleteAsync(receivedMessage);

                            dynamic messageJSON = JObject.Parse(messageContent);
                            string command = messageJSON.command;

                            foreach (var msgCmd in _MessageCommandList)
                            {
                                if (msgCmd.command.ToLower() == command.ToLower())
                                {
                                    foreach (var act in msgCmd.actions)
                                    {
                                        string setValue = "";
                                        Match match = Regex.Match(act.dataValue, @"\[.*?\]");
                                        if (match.Success)
                                        {
                                            string inputVariable = act.dataValue.Substring(1, act.dataValue.Length - 2);
                                            setValue = messageJSON[inputVariable];
                                        }
                                        else
                                            setValue = act.dataValue;

                                        if (!DoOPCUAWrite(act.nodeId, act.dataType, setValue))
                                            break;
                                        else
                                            Thread.Sleep(UACommandInterval);
                                    }
                                    //DoOPCUABatchWrite(msgCmd.actions);
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (ex is IotHubException || ex is IotHubCommunicationException || ex is IotHubNotFoundException)
                            {
                                Console.WriteLine("IoT Hub Exception: " + ex.Message);
                                break;
                            }
                            else if (ex is Opc.Ua.ServiceResultException)
                            {
                                Console.WriteLine("OPC UA Exception: " + ex.Message);
                                break;
                            }
                            else if (ex.Message.Contains("Operation timeout expired."))
                            {
                                Console.WriteLine("Timeout Exception: " + ex.Message);
                                break;
                            }
                            else
                                Console.WriteLine("Exception: " + ex.Message);
                        }
                    }
                }
            }
            return true;
        }

        private void retriveNodeIdDataType(ref List<CommandActions> commandActionList)
        {
            ReadValueIdCollection nodesForRead = new ReadValueIdCollection();

            foreach (var msgCmd in commandActionList)
                foreach (var action in msgCmd.actions)
                {
                    ReadValueId nofeForRead = new ReadValueId();
                    nofeForRead.AttributeId = Attributes.Value;
                    nofeForRead.NodeId = action.nodeId;
                    nodesForRead.Add(nofeForRead);
                }
            DataValueCollection dataValueList = _uaClient.Read(nodesForRead);

            int index = 0;
            foreach (var msgCmd in commandActionList)
                foreach (var action in msgCmd.actions)
                {
                    if (dataValueList[index] != null)
                    {
                        action.dataType = dataValueList[index].WrappedValue.TypeInfo.BuiltInType;
                    }
                    index++;
                }
        }

        private void OpcUAServerInitialCommand()
        {
            if (_initialCommandList != null)
            {
                foreach (var initCmd in _initialCommandList)
                {
                    if (initCmd.actions != null)
                    {
                        foreach (var act in initCmd.actions)
                        {
                            if (!DoOPCUAWrite(act.nodeId, act.dataType, act.dataValue))
                                break;
                            Thread.Sleep(UACommandInterval);
                        }
                        break;
                    }
                }
            }

        }

        // Start: {"command":"start"}
        // Set Speed: {"command":"set speed", "xxx":100}
        // Stop : {"command":"stop"}
        private void loadCommandConfiguration()
        {
            if (File.Exists(_CDSConfigurationFilename))
            {
                dynamic Entries = JObject.Parse(File.ReadAllText(_CDSConfigurationFilename));

                _initialCommandList = new List<CommandActions>();
                if (Entries.OPCUAInitialCommand != null)
                {
                    foreach (var initCmd in Entries.OPCUAInitialCommand)
                    {
                        CommandActions initCommand = new CommandActions();
                        initCommand.command = initCmd.command;
                        initCommand.actions = new List<action>();
                        foreach (var action in initCmd.actions)
                        {
                            action act = new CDSProxy.action();
                            act.nodeId = action.NodeId;
                            act.dataValue = action.Value;
                            initCommand.actions.Add(act);
                        }
                        _initialCommandList.Add(initCommand);
                    }
                }

                _MessageCommandList = new List<CommandActions>();
                foreach (var c2dmsg in Entries.C2DMessage)
                {
                    CommandActions msgCommand = new CommandActions();
                    msgCommand.command = c2dmsg.command;
                    msgCommand.actions = new List<action>();
                    foreach (var action in c2dmsg.actions)
                    {
                        action act = new CDSProxy.action();
                        act.nodeId = action.NodeId;
                        act.dataValue = action.Value;
                        msgCommand.actions.Add(act);
                    }
                    _MessageCommandList.Add(msgCommand);
                }
            }
            else
            {
                throw new Exception("Can't load File:CDSConfiguration.json");
            }
        }

        private void DoOPCUABatchWrite(List<action> actions)
        {
            WriteValueCollection nodesToWrite = new WriteValueCollection();
            foreach (var action in actions)
            {
                WriteValue nodeToWrite = new WriteValue();
                nodeToWrite.AttributeId = Attributes.Value;
                nodeToWrite.NodeId = new NodeId(action.nodeId);
                switch (action.dataType)
                {
                    case BuiltInType.Boolean:
                        nodeToWrite.Value.Value = bool.Parse(action.dataValue);
                        break;
                    case BuiltInType.LocalizedText:
                    case BuiltInType.String:
                    case BuiltInType.XmlElement:
                        nodeToWrite.Value.Value = action.dataValue;
                        break;
                    case BuiltInType.Int16:
                        nodeToWrite.Value.Value = Int16.Parse(action.dataValue);
                        break;
                    case BuiltInType.Int32:
                        nodeToWrite.Value.Value = Int32.Parse(action.dataValue);
                        break;
                    case BuiltInType.Int64:
                        nodeToWrite.Value.Value = Int64.Parse(action.dataValue);
                        break;
                    case BuiltInType.UInt16:
                        nodeToWrite.Value.Value = UInt16.Parse(action.dataValue);
                        break;
                    case BuiltInType.UInt32:
                        nodeToWrite.Value.Value = UInt32.Parse(action.dataValue);
                        break;
                    case BuiltInType.UInt64:
                        nodeToWrite.Value.Value = UInt64.Parse(action.dataValue);
                        break;
                    case BuiltInType.Double:
                        nodeToWrite.Value.Value = Double.Parse(action.dataValue);
                        break;
                    case BuiltInType.Float:
                        nodeToWrite.Value.Value = Single.Parse(action.dataValue);
                        break;
                    case BuiltInType.DateTime:
                        nodeToWrite.Value.Value = DateTime.Parse(action.dataValue);
                        break;
                }
                nodeToWrite.Value.StatusCode = StatusCodes.Good;
                nodeToWrite.Value.ServerTimestamp = DateTime.MinValue;
                nodeToWrite.Value.SourceTimestamp = DateTime.MinValue;
                nodesToWrite.Add(nodeToWrite);                
            }
            StatusCodeCollection statusCollection = _uaClient.Write(nodesToWrite);
            Console.WriteLine("Write Result:" + statusCollection[0].ToString());
        }

        private bool DoOPCUAWrite(string NodeId, BuiltInType DataType, string setValue)
        {
            if (string.IsNullOrEmpty(NodeId) || DataType == BuiltInType.Null || string.IsNullOrEmpty(setValue))
            {
                Console.WriteLine(string.Format("Exception on DoOPCUAWrite. Input variable now allow null. NodeId:{0}, DataType:{1}, Value:{2}.", NodeId, DataType, setValue));
                return false;
            }

            Console.WriteLine(string.Format("Write to OPC UA. NodeId:{0}, DataType:{1}, Value:{2}.", NodeId, DataType, setValue));

            WriteValueCollection nodesToWrite = new WriteValueCollection();
            WriteValue nodeToWrite = new WriteValue();
            nodeToWrite.AttributeId = Attributes.Value;
            nodeToWrite.NodeId = new NodeId(NodeId);

            switch (DataType)
            {
                case BuiltInType.Boolean:
                    nodeToWrite.Value.Value = bool.Parse(setValue);
                    break;
                case BuiltInType.LocalizedText:
                case BuiltInType.String:
                case BuiltInType.XmlElement:
                    nodeToWrite.Value.Value = setValue;
                    break;
                case BuiltInType.Int16:
                    nodeToWrite.Value.Value = Int16.Parse(setValue);
                    break;
                case BuiltInType.Int32:
                    nodeToWrite.Value.Value = Int32.Parse(setValue);
                    break;
                case BuiltInType.Int64:
                    nodeToWrite.Value.Value = Int64.Parse(setValue);
                    break;
                case BuiltInType.UInt16:
                    nodeToWrite.Value.Value = UInt16.Parse(setValue);
                    break;
                case BuiltInType.UInt32:
                    nodeToWrite.Value.Value = UInt32.Parse(setValue);
                    break;
                case BuiltInType.UInt64:
                    nodeToWrite.Value.Value = UInt64.Parse(setValue);
                    break;
                case BuiltInType.Double:
                    nodeToWrite.Value.Value = Double.Parse(setValue);
                    break;
                case BuiltInType.Float:
                    nodeToWrite.Value.Value = Single.Parse(setValue);
                    break;
                case BuiltInType.DateTime:
                    nodeToWrite.Value.Value = DateTime.Parse(setValue);
                    break;
            }
            nodeToWrite.Value.StatusCode = StatusCodes.Good;
            nodeToWrite.Value.ServerTimestamp = DateTime.MinValue;
            nodeToWrite.Value.SourceTimestamp = DateTime.MinValue;
            nodesToWrite.Add(nodeToWrite);

            StatusCodeCollection statusCollection = _uaClient.Write(nodesToWrite);
            Console.WriteLine("Write Result:" + statusCollection[0].ToString());

            if (statusCollection[0].ToString().ToLower() == "good")
                return true;
            else
                return false;
        }
    }
}
