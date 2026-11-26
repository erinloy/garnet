// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.devices
{
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure.Core.Pipeline;

    /// <summary>
    /// The process's one HTTP transport to blob storage. Azure.Core's default shared client sets no per-server connection
    /// bound on .NET, so writes and leases (which take no <see cref="BlobReadLanes"/> turn) opened 2,671 connections in one
    /// 8-node test process. Every blob client of the device and of its host takes this transport, so the process holds at most
    /// <see cref="MaxConnections"/> per server: the read lanes' permits, and as many again for everything else.
    /// </summary>
    public static class BlobTransport
    {
        static long open;

        public static int MaxConnections => 2 * BlobReadLanes.DefaultReadPermits;

        /// <summary>Connections this process's transport holds open now, to every server.</summary>
        public static long OpenConnections => Interlocked.Read(ref open);

        public static HttpPipelineTransport Shared { get; } = new HttpClientTransport(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = MaxConnections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),   // a pooled connection re-resolves DNS, as Azure.Core's default does
            ConnectCallback = ConnectAsync,
        });

        static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false); }
            catch { socket.Dispose(); throw; }
            Interlocked.Increment(ref open);
            return new Counted(socket);
        }

        sealed class Counted : NetworkStream
        {
            int closed;

            public Counted(Socket socket) : base(socket, ownsSocket: true) { }

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref closed, 1) == 0) Interlocked.Decrement(ref open);
                base.Dispose(disposing);
            }
        }
    }
}
