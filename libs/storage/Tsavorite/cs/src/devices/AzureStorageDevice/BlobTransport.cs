// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.devices
{
    using System;
    using System.Net.Http;
    using Azure.Core.Pipeline;

    /// <summary>
    /// The process's one HTTP transport to blob storage. Azure.Core's default shared client sets no per-server connection
    /// bound on .NET, so writes and leases (which take no <see cref="BlobReadLanes"/> turn) opened 2,671 connections in one
    /// 8-node test process. Every blob client of the device and of its host takes this transport, so the process holds at most
    /// <see cref="MaxConnections"/> per server: the read lanes' permits, and as many again for everything else.
    /// </summary>
    public static class BlobTransport
    {
        public static int MaxConnections => 2 * BlobReadLanes.DefaultReadPermits;

        public static HttpPipelineTransport Shared { get; } = new HttpClientTransport(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = MaxConnections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),   // a pooled connection re-resolves DNS, as Azure.Core's default does
        });
    }
}
