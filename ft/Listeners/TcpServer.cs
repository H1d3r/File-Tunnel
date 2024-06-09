﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace ft.Listeners
{
    public class TcpServer(string endpointStr) : StreamEstablisher
    {
        TcpListener? listener;
        Thread? listenerTask;

        public string EndpointStr { get; } = endpointStr;

        public override void Start()
        {
            var listenEndpoint = IPEndPoint.Parse(EndpointStr);

            listenerTask = Threads.StartNew(() =>
            {
                try
                {
                    listener = new TcpListener(listenEndpoint);
                    listener.Start();
                    Program.Log($"Listening on {EndpointStr}");

                    while (true)
                    {
                        var client = listener.AcceptTcpClient();

                        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";

                        Program.Log($"Accepted connection from {client.Client.RemoteEndPoint}");

                        var clientStream = client.GetStream();

                        StreamEstablished?.Invoke(this, clientStream);
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"TcpServer error: {ex.Message}");
                }
            }, $"TCP listener {EndpointStr}");
        }

        public override void Stop()
        {
            try
            {
                listener?.Stop();
            }
            catch (Exception ex)
            {
                Program.Log($"Stop(): {ex}");
            }


            try
            {
                listenerTask?.Join();
            }
            catch (Exception ex)
            {
                Program.Log($"Stop(): {ex}");
            }

        }
    }
}
