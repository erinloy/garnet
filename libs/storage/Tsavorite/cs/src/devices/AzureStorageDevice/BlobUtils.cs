// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.devices
{
    using System;
    using System.Threading.Tasks;

    static class BlobUtils
    {
        /// <summary>
        /// Checks whether the given storage exception is transient, and 
        /// therefore meaningful to retry.
        /// </summary>
        /// <param name="exception">The storage exception.</param>
        /// <returns>Whether this is a transient storage exception.</returns>
        public static bool IsTransientStorageError(Exception exception)
        {
            // handle Azure V12 SDK exceptions
            if (exception is Azure.RequestFailedException e1 && httpStatusIndicatesTransientError(e1.Status))
            {
                return true;
            }

            // ZILTCH 2026-10-05: A BLOB THAT COULD NOT BE REACHED OR AUTHENTICATED RIGHT NOW IS NOT A FAILED STORAGE OPERATION.
            // Measured on the webfrontend (cell-20261004.log 11:17:48Z and 22:08Z, cell-20261005.log): the Azure CLI credential
            // timed out ("Azure CLI authentication timed out", a box starved of commit) inside a CRITICAL page write. That is not a
            // RequestFailedException, so it was not transient; PerformWithRetriesAsync took it for fatal, HandleStorageError
            // terminated the partition, and every later operation on that log device was cancelled with no exception text
            // (callback uint.MaxValue, "error code -1") for the rest of the process life: FlushedUntilAddress never advanced,
            // the checkpoint coordinator failed 39 times in a row, and the store could not persist (the log buffer fills, then
            // writers wedge). The tier's own credential and lease code already say a slow CLI is not a failed login
            // (ColdTierCredential, RgColdTier.IsLeaseUnverifiable); the device now agrees, for the same family: a credential that
            // would not mint, and a request that never got an answer (RequestFailedException with Status 0, HttpRequestException,
            // SocketException: DNS, reset, refused). A genuine refusal (403, 404, a failed precondition) is still not transient.
            if (IsEnvironmentUnavailable(exception))
            {
                return true;
            }

            // Empirically observed: timeouts on synchronous calls
            if (exception.InnerException is TimeoutException)
            {
                return true;
            }

            // Empirically observed: transient cancellation exceptions that are not application initiated
            if (exception is OperationCanceledException || exception.InnerException is OperationCanceledException)
            {
                return true;
            }

            // Empirically observed: transient exception ('An existing connection was forcibly closed by the remote host')
            if (exception.InnerException is System.Net.Http.HttpRequestException && exception.InnerException?.InnerException is System.IO.IOException)
            {
                return true;
            }

            // Empirically observed: transient socket exceptions
            if (exception is System.IO.IOException && exception.InnerException is System.Net.Sockets.SocketException)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// ZILTCH: the environment could not answer right now: a credential that would not mint a token, or a request that got no
        /// HTTP answer at all. Walks the inner chain (the Azure pipeline wraps transport failures). By type name for the credential
        /// family because this assembly does not reference Azure.Identity. A 401 is in the family: the service did not take the
        /// caller's token (one that expired while the machine slept), and the next attempt authenticates again. A 403 is not:
        /// the caller is known and refused.
        /// </summary>
        public static bool IsEnvironmentUnavailable(Exception exception)
        {
            for (var e = exception; e != null; e = e.InnerException)
            {
                if (e is Azure.RequestFailedException { Status: 0 or 401 }
                    || e is System.Net.Http.HttpRequestException
                    || e is System.Net.Sockets.SocketException)
                {
                    return true;
                }

                for (var t = e.GetType(); t != null; t = t.BaseType)
                {
                    if (t.FullName is "Azure.Identity.AuthenticationFailedException" or "Azure.Identity.CredentialUnavailableException")
                    {
                        return true;
                    }
                }

                if (e is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        if (IsEnvironmentUnavailable(inner))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether the given exception is a timeout exception.
        /// </summary>
        /// <param name="exception">The exception.</param>
        /// <returns>Whether this is a timeout storage exception.</returns>
        public static bool IsTimeout(Exception exception)
        {
            return exception is TimeoutException
                || (exception is Azure.RequestFailedException e1 && (e1.Status == 408 || e1.ErrorCode == "OperationTimedOut"))
                || (exception is TaskCanceledException & exception.Message.StartsWith("The operation was cancelled because it exceeded the configured timeout"));
        }

        // Transient http status codes as documented at https://docs.microsoft.com/en-us/azure/architecture/best-practices/retry-service-specific#azure-storage
        static bool httpStatusIndicatesTransientError(int? statusCode) =>
            (statusCode == 408    //408 Request Timeout
            || statusCode == 429  //429 Too Many Requests
            || statusCode == 500  //500 Internal Server Error
            || statusCode == 502  //502 Bad Gateway
            || statusCode == 503  //503 Service Unavailable
            || statusCode == 504); //504 Gateway Timeout
    }
}