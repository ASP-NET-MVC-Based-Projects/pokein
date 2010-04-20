﻿/* 
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License along
 * with this program; if not, write to the Free Software Foundation, Inc.,
 * 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA.

 * 
 * PokeIn Comet Library
 * Copyright © 2010 http://pokein.codeplex.com (info@pokein.com)
 */
using System;
using System.Collections.Generic;
using System.Text;
using System.Web;

namespace PokeIn.Comet
{
    public delegate void DefineClassObjects(string clientId, ref Dictionary<string, object> classList);

    public class CometWorker
    {
        static PokeIn.DynamicCode Code = null;
        static Dictionary<string, CometMessage> Clients = null;
        static Dictionary<string, List<string>> ClientScriptsLog = null; 
        public static Dictionary<string, ClientCodeStatus> ClientStatus = null;

        static void CheckStaticCreations()
        {
            if (Code == null)
            {
                Code = new PokeIn.DynamicCode();
            }
            if (Clients == null)
            {
                Clients = new Dictionary<string, CometMessage>();
            }
            if (ClientStatus == null)
            {
                ClientStatus = new Dictionary<string, ClientCodeStatus>();
            }
            if (ClientScriptsLog == null)
            {
                ClientScriptsLog = new Dictionary<string, List<string>>();
            }
        }

        public static bool Bind(string listenUrl, string sendUrl, System.Web.UI.Page page, DefineClassObjects classDefs, out string clientId)
        {
            clientId = "C" + DateTime.Now.ToFileTime().ToString();

            CheckStaticCreations();

            lock (Clients)
            {
                Clients.Add(clientId, new CometMessage(clientId));
            }

            Dictionary<string, object> classList = new Dictionary<string, object>();
            classDefs(clientId, ref classList);

            bool anyAdd = false;

            lock (PokeIn.DynamicCode.Definitions)
            {
                foreach (KeyValuePair<string, object> en in classList)
                {
                    object ba = en.Value;
                    PokeIn.DynamicCode.Definitions.Add(en.Key, ref ba, clientId);
                    anyAdd = true;
                }
                object brO = new BrowserEvents(clientId);
                DynamicCode.Definitions.Add("BrowserEvents", ref brO, clientId);
            }

            if (!anyAdd)
            {
                page.Response.Write("<script>alert('EvaluateSettings class is empty');</script>");
                return false;
            }

            JWriter.WriteClientScript(ref page, clientId, listenUrl, sendUrl); 

            CometWorker worker = new CometWorker(clientId);
            System.Threading.ThreadStart Ts = new System.Threading.ThreadStart(worker.ClientThread);
            worker._Thread = new System.Threading.Thread(Ts);
            worker._Thread.Start();

            lock (ClientStatus)
            {
                ClientStatus.Add(clientId, new ClientCodeStatus(ref worker));
            }

            return true;
        }

        #region non-static

        string ClientId;

        System.Threading.Thread _Thread;

        CometWorker(string clientId) { ClientId = clientId; CodesToRun = new List<string>(); }

        List<string> CodesToRun;

        void ClientThread()
        {
            int roundCounter = 1;
            int totalWait = 0;
            while (true)
            {
                int record_count = 0;
                lock (CodesToRun)
                {
                    record_count = CodesToRun.Count;
                }
                if (record_count == 0)
                {
                    int _timer = 30 + ( (roundCounter / 1000) * 50 );
                    totalWait += _timer;
                    System.Threading.Thread.Sleep(_timer);
                    roundCounter++;

                    if (totalWait > CometSettings.ClientTimeout)
                    {
                        SendToClient(ClientId, "PokeIn.Closed(;)");
                        break;
                    }
                    if (roundCounter % 1000 == 0)
                    {
                        bool hasClient = false;
                        lock (ClientStatus)
                        {
                            hasClient = ClientStatus.ContainsKey(ClientId);
                        }
                        if (hasClient)
                        {
                            lock (ClientStatus[ClientId])
                            {
                                if (ClientStatus[ClientId].Online < DateTime.Now.AddMilliseconds(-1 * CometSettings.ConnectionLostTimeout))
                                {
                                    totalWait = CometSettings.ClientTimeout + 1;
                                    SendToClient(ClientId, "PokeIn.Closed();");
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                {
                    totalWait = 0;
                    roundCounter = 1;
                    string code = "";
                    lock (CodesToRun)
                    {
                        for (int i = 0; i < record_count; i++)
                        {
                            if (i != 0)
                            {
                                code += "\r";
                            }
                            code += CodesToRun[i];
                        }
                        CodesToRun.Clear();
                    }
                    if (code.Length>0)
                    {
                        if (!Code.Run(code))
                        {
                            SendToClient(ClientId, "PokeIn.CompilerError('" + CometWorker.Code.ErrorMessage + " :: " + code + "');");
                        }
                    }
                }
            }

            try
            {
                if (totalWait > CometSettings.ClientTimeout)
                    RemoveClient(ClientId);
            }
            catch (System.Exception)
            {
            }
        }
        #endregion

        static string CreateText(string clientId, string mess, bool _in)
        { 
            string clide = clientId.Substring(1, clientId.Length - 1);
            string []lst = new string[5] { ".", "(", ")", "{", "}"  };
            if (_in) {
                for (var i = 0; i < 5; i++) {
                    mess = mess.Replace(":" + clide + i.ToString() + ":", lst[i]);
                }
            }
            else {
                for (var i = 0; i < 5; i++) {
                    mess = mess.Replace(lst[i], ":" + clide + i.ToString() + ":");
                }
            } 
            return mess;
        }

        public static void Send(System.Web.UI.Page page)
        {
            if (!page.Request.Params.HasKeys())
                return;

            string clientId = page.Request.Params["c"];
            if (clientId == null)
            {
                return;
            }
            
            string message = page.Request.Params["ms"];

            if (message.Trim().Length == 0)
            {
                return;
            }

            message = CreateText(clientId, message, true);

            if (CometSettings.LogClientScripts)
            {
                lock (ClientScriptsLog)
                {
                    if (!ClientScriptsLog.ContainsKey(clientId))
                    {
                        ClientScriptsLog.Add(clientId, new List<string>());
                    }
                }
                lock(ClientScriptsLog[clientId])
                {
                    ClientScriptsLog[clientId].Add(message);
                }
            }

            bool hasClient = false;
            lock (ClientStatus)
            {
                hasClient = ClientStatus.ContainsKey(clientId);
            }

            if (hasClient)
            {
                lock (ClientStatus[clientId])
                {
                    ClientStatus[clientId].Online = DateTime.Now;
                } 

                if (message.Trim().StartsWith(clientId + ".CometBase.Close();"))
                {
                    RemoveClient(clientId);
                    page.Response.Write("PokeIn.Closed();");
                    page.Response.Flush();
                }
                else
                {
                    lock (ClientStatus[clientId])
                    {
                        ClientStatus[clientId].Worker.CodesToRun.Add(message);
                    } 

                    string messages = "";
                    if (GrabClientMessages(clientId, out messages))
                    {
                        if (messages.Length > 0)
                        {
                            page.Response.Write(messages);
                        }
                    }
                    else if (messages == null)
                    {
                        RemoveClient(clientId);
                        page.Response.Write("PokeIn.ClientObjectsDoesntExist();");
                        page.Response.Write("PokeIn.Closed();");
                        page.Response.Flush();
                    } 
                }
            }
            else
            {
                RemoveClient(clientId);
                page.Response.Write("PokeIn.ClientObjectsDoesntExist();");
                page.Response.Flush();
            } 
        }

        public static void Listen(System.Web.UI.Page page)
        {
            string clientId = page.Request.Params["c"];

            if (clientId == null)
            {
                return;
            }
            DateTime pageStart = DateTime.Now.AddMilliseconds(CometSettings.ListenerTimeout);

            bool hasClient = false;

            lock (ClientStatus)
            {
                hasClient = ClientStatus.ContainsKey(clientId);
            }

            if (hasClient)
            {
                lock (ClientStatus[clientId])
                {
                    ClientStatus[clientId].Online = DateTime.Now;
                }
            }

            while (true)
            {
                string messages = "";
                if (GrabClientMessages(clientId, out messages))
                {
                    if (messages.Length > 0)
                    {
                        page.Response.Write(CreateText(clientId, messages + "PokeIn.Listen();", false)); 
                        break;
                    }
                }
                else if (messages == null)
                {
                    RemoveClient(clientId);
                    page.Response.Write(CreateText(clientId, "PokeIn.ClientObjectsDoesntExist();PokeIn.Closed();", false)); 
                    break;
                }

                if (pageStart < DateTime.Now)
                {
                    page.Response.Write(CreateText(clientId, "PokeIn.Listen();", false)); 
                    break;
                }
                System.Threading.Thread.Sleep(50);
            }
        } 

        public static void SendToClient(string clientId, string message)
        {
            bool hasClient = false;

            lock (ClientStatus)
            {
                hasClient = ClientStatus.ContainsKey(clientId);
            }

            if (hasClient)
            {
                lock (Clients[clientId])
                {
                    Clients[clientId].PushMessage(message);
                }
            }
        }

        public static void SendToAll(string message)
        {
            foreach (string clientId in Clients.Keys)
            {
                SendToClient(clientId, message);
            }
        }
        public static string[] GetClientIds()
        {
            string[] clientIds = null;
            lock (Clients)
            {
               clientIds = new string[Clients.Keys.Count];
               Clients.Keys.CopyTo(clientIds, 0);
            }
            return clientIds;
        }

        public static void SendToClients(string[] clientIds, string message)
        {
            for (int i = 0, lmt = clientIds.Length; i < lmt; i++)
            {
                SendToClient(clientIds[i], message);
            }
        }

        static bool GrabClientMessages(string clientId, out string message)
        {
            bool hasClient = false;

            lock (ClientStatus)
            {
                hasClient = ClientStatus.ContainsKey(clientId);
            }

            if (hasClient)
            {
                lock (Clients[clientId])
                {
                    Clients[clientId].PullMessages(out message);
                }
                return true;
            } 
            message = null;
            return false;
        }

        public static void RemoveClient(string clientId)
        {
            bool hasClient = false;

            lock (ClientStatus)
            {
                hasClient = ClientStatus.ContainsKey(clientId);
            }

            if (hasClient)
            {
                lock (ClientStatus[clientId])
                {
                    try
                    {
                        ClientStatus[clientId].Worker._Thread.Abort();
                    }
                    catch (Exception) { }
                    try
                    {
                        ClientStatus[clientId].Worker.CodesToRun.Clear();
                    }
                    catch (Exception) { }
                    try
                    {
                        ClientStatus[clientId].Events.Clear();
                    }
                    catch (Exception) { }
                    ClientStatus[clientId].Worker = null;
                }
                lock (ClientStatus)
                {
                    ClientStatus.Remove(clientId);
                }
            }

            lock (Clients)
            {
                Clients.Remove(clientId);
            }

            List<string> lstKeys = new List<string>();
            lock (PokeIn.DynamicCode.Definitions)
            {
                PokeIn.DynamicCode.Definitions.definedClasses.Remove(clientId);
                foreach (string key in PokeIn.DynamicCode.Definitions.classObjects.Keys)
                {
                    if (key.StartsWith(clientId + "."))
                    {
                        lstKeys.Add(key);
                    }
                }
                foreach (string key in lstKeys)
                    PokeIn.DynamicCode.Definitions.classObjects.Remove(key);
            }

            lstKeys.Clear();
        }

        public class ClientLog
        {
            public static string[] GetClientScriptLog(string clientId)
            {
                string[] arrLog = null;
                if(CometSettings.LogClientScripts)
                {
                    bool hasClient = false;

                    lock (CometWorker.ClientScriptsLog)
                    {
                        hasClient = CometWorker.ClientScriptsLog.ContainsKey(clientId);
                    }

                    if (hasClient)
                    {
                        lock (CometWorker.ClientScriptsLog[clientId])
                        {
                            arrLog = CometWorker.ClientScriptsLog[clientId].ToArray();
                        }
                    }
                }

                return arrLog;
            }

            public static void ClearClientScriptLog(string clientId)
            {
                if(CometSettings.LogClientScripts)
                {
                    bool hasClient = false;

                    lock (CometWorker.ClientScriptsLog)
                    {
                        hasClient = CometWorker.ClientScriptsLog.ContainsKey(clientId);
                    }

                    if (hasClient)
                    {
                        lock (CometWorker.ClientScriptsLog[clientId])
                        {
                            CometWorker.ClientScriptsLog[clientId].Clear();
                        }
                    }
                }
            }
        }
    }
}