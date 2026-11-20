// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Tsavorite.devices
{
    public class AzureStorageNamedDeviceFactoryCreator : INamedDeviceFactoryCreator
    {
        readonly BlobUtilsV12.ServiceClients pageBlobAccount;
        readonly ILogger logger;

        /// <summary>
        /// Creator of factory for Azure devices
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="logger"></param>
        public AzureStorageNamedDeviceFactoryCreator(string connectionString, ILogger logger = null)
            : this(BlobUtilsV12.GetServiceClients(connectionString), logger)
        {
        }

        /// <summary>
        /// Create instance of factory for Azure devices
        /// </summary>
        /// <param name="serviceUri"></param>
        /// <param name="credential"></param>
        /// <param name="logger"></param>
        public AzureStorageNamedDeviceFactoryCreator(string serviceUri, TokenCredential credential, ILogger logger = null)
            : this(BlobUtilsV12.GetServiceClients(serviceUri, credential), logger)
        {
        }

        /// <summary>
        /// Creator of factory for Azure devices
        /// </summary>
        /// <param name="pageBlobAccount"></param>
        /// <param name="logger"></param>
        AzureStorageNamedDeviceFactoryCreator(BlobUtilsV12.ServiceClients pageBlobAccount, ILogger logger = null)
        {
            this.pageBlobAccount = pageBlobAccount;
            this.logger = logger;
        }

        /// <summary>
        /// ZILTCH (echo #90130 (1)): the writer fence every device this creator's factories make asks before each page PUT
        /// (<see cref="AzureStorageDevice.WriteFence"/>). Null: unfenced.
        /// </summary>
        public Func<string> WriteFence { get; set; }

        /// <summary>ZILTCH (echo #94774 (2)): the writer's segment lease id for every device this creator's factories make
        /// (<see cref="AzureStorageDevice.SegmentLeaseId"/>). Null: unleased.</summary>
        public Func<string> SegmentLeaseId { get; set; }

        public INamedDeviceFactory Create(string baseName)
        {
            return new AzureStorageNamedDeviceFactory(baseName, pageBlobAccount, logger) { WriteFence = WriteFence, SegmentLeaseId = SegmentLeaseId };
        }
    }
}