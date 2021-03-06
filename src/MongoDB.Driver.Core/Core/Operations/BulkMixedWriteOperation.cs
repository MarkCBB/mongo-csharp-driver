﻿/* Copyright 2010-2014 MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver.Core.Bindings;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Core.WireProtocol.Messages.Encoders;

namespace MongoDB.Driver.Core.Operations
{
    public class BulkMixedWriteOperation : IWriteOperation<BulkWriteOperationResult>
    {
        // fields
        private readonly CollectionNamespace _collectionNamespace;
        private bool _isOrdered = true;
        private int? _maxBatchCount;
        private int? _maxBatchLength;
        private int? _maxDocumentSize;
        private int? _maxWireDocumentSize;
        private readonly MessageEncoderSettings _messageEncoderSettings;
        private readonly IEnumerable<WriteRequest> _requests;
        private WriteConcern _writeConcern;

        // constructors
        public BulkMixedWriteOperation(
            CollectionNamespace collectionNamespace,
            IEnumerable<WriteRequest> requests,
            MessageEncoderSettings messageEncoderSettings)
        {
            _collectionNamespace = Ensure.IsNotNull(collectionNamespace, "collectionNamespace");
            _requests = Ensure.IsNotNull(requests, "requests");
            _messageEncoderSettings = Ensure.IsNotNull(messageEncoderSettings, "messageEncoderSettings");
            _writeConcern = WriteConcern.Acknowledged;
        }

        // properties
        public CollectionNamespace CollectionNamespace
        {
            get { return _collectionNamespace; }
        }

        public bool IsOrdered
        {
            get { return _isOrdered; }
            set { _isOrdered = value; }
        }

        public int? MaxBatchCount
        {
            get { return _maxBatchCount; }
            set { _maxBatchCount = Ensure.IsNullOrGreaterThanZero(value, "value"); }
        }

        public int? MaxBatchLength
        {
            get { return _maxBatchLength; }
            set { _maxBatchLength = Ensure.IsNullOrGreaterThanZero(value, "value"); }
        }

        public int? MaxDocumentSize
        {
            get { return _maxDocumentSize; }
            set { _maxDocumentSize = Ensure.IsNullOrGreaterThanZero(value, "value"); }
        }

        public int? MaxWireDocumentSize
        {
            get { return _maxWireDocumentSize; }
            set { _maxWireDocumentSize = Ensure.IsNullOrGreaterThanZero(value, "value"); }
        }

        public MessageEncoderSettings MessageEncoderSettings
        {
            get { return _messageEncoderSettings; }
        }

        public IEnumerable<WriteRequest> Requests
        {
            get { return _requests; }
        }

        public WriteConcern WriteConcern
        {
            get { return _writeConcern; }
            set { _writeConcern = Ensure.IsNotNull(value, "value"); }
        }

        // methods
        public async Task<BulkWriteOperationResult> ExecuteAsync(IConnectionHandle connection, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var slidingTimeout = new SlidingTimeout(timeout);
            var batchResults = new List<BulkWriteBatchResult>();
            var remainingRequests = Enumerable.Empty<WriteRequest>();
            var hasWriteErrors = false;

            var runCount = 0;
            var maxRunLength = Math.Min(_maxBatchCount ?? int.MaxValue, connection.Description.MaxBatchCount);
            foreach (var run in FindRuns(maxRunLength))
            {
                runCount++;

                if (hasWriteErrors && _isOrdered)
                {
                    remainingRequests = remainingRequests.Concat(run.Requests);
                    continue;
                }

                var batchResult = await ExecuteBatchAsync(connection, run, slidingTimeout, cancellationToken).ConfigureAwait(false);
                batchResults.Add(batchResult);

                hasWriteErrors |= batchResult.HasWriteErrors;
            }

            if (runCount == 0)
            {
                throw new InvalidOperationException("Bulk write operation is empty.");
            }

            var combiner = new BulkWriteBatchResultCombiner(batchResults, _writeConcern.IsAcknowledged);
            return combiner.CreateResultOrThrowIfHasErrors(remainingRequests.ToList());
        }

        public async Task<BulkWriteOperationResult> ExecuteAsync(IWriteBinding binding, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var slidingTimeout = new SlidingTimeout(timeout);
            using (var connectionSource = await binding.GetWriteConnectionSourceAsync(slidingTimeout, cancellationToken).ConfigureAwait(false))
            using (var connection = await connectionSource.GetConnectionAsync(slidingTimeout, cancellationToken).ConfigureAwait(false))
            {
                return await ExecuteAsync(connection, slidingTimeout, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<BulkWriteBatchResult> ExecuteBatchAsync(IConnectionHandle connection, Run run, TimeSpan timeout, CancellationToken cancellationToken)
        {
            BulkWriteOperationResult result;
            BulkWriteOperationException exception = null;
            try
            {
                switch (run.RequestType)
                {
                    case WriteRequestType.Delete:
                        result = await ExecuteDeletesAsync(connection, run.Requests.Cast<DeleteRequest>(), timeout, cancellationToken).ConfigureAwait(false);
                        break;
                    case WriteRequestType.Insert:
                        result = await ExecuteInsertsAsync(connection, run.Requests.Cast<InsertRequest>(), timeout, cancellationToken).ConfigureAwait(false);
                        break;
                    case WriteRequestType.Update:
                        result = await ExecuteUpdatesAsync(connection, run.Requests.Cast<UpdateRequest>(), timeout, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new MongoInternalException("Unrecognized RequestType.");
                }
            }
            catch (BulkWriteOperationException ex)
            {
                result = ex.Result;
                exception = ex;
            }

            return BulkWriteBatchResult.Create(result, exception, run.IndexMap);
        }

        private Task<BulkWriteOperationResult> ExecuteDeletesAsync(IConnectionHandle connection, IEnumerable<DeleteRequest> requests, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var operation = new BulkDeleteOperation(_collectionNamespace, requests, _messageEncoderSettings)
            {
                MaxBatchCount = _maxBatchCount,
                MaxBatchLength = _maxBatchLength,
                WriteConcern = _writeConcern
            };
            return operation.ExecuteAsync(connection, timeout, cancellationToken);
        }

        private Task<BulkWriteOperationResult> ExecuteInsertsAsync(IConnectionHandle connection, IEnumerable<InsertRequest> requests, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var operation = new BulkInsertOperation(_collectionNamespace, requests, _messageEncoderSettings)
            {
                MaxBatchCount = _maxBatchCount,
                MaxBatchLength = _maxBatchLength,
                IsOrdered = _isOrdered,
                MessageEncoderSettings = _messageEncoderSettings,
                WriteConcern = _writeConcern
            };
            return operation.ExecuteAsync(connection, timeout, cancellationToken);
        }

        private Task<BulkWriteOperationResult> ExecuteUpdatesAsync(IConnectionHandle connection, IEnumerable<UpdateRequest> requests, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var operation = new BulkUpdateOperation(_collectionNamespace, requests, _messageEncoderSettings)
            {
                MaxBatchCount = _maxBatchCount,
                MaxBatchLength = _maxBatchLength,
                IsOrdered = _isOrdered,
                WriteConcern = _writeConcern
            };
            return operation.ExecuteAsync(connection, timeout, cancellationToken);
        }

        private IEnumerable<Run> FindOrderedRuns(int maxRunLength)
        {
            Run run = null;

            var originalIndex = 0;
            foreach (var request in _requests)
            {
                if (run == null)
                {
                    run = new Run();
                    run.Add(request, originalIndex);
                }
                else if (run.RequestType == request.RequestType)
                {
                    if (run.Count == maxRunLength)
                    {
                        yield return run;
                        run = new Run();
                    }
                    run.Add(request, originalIndex);
                }
                else
                {
                    yield return run;
                    run = new Run();
                    run.Add(request, originalIndex);
                }

                originalIndex++;
            }

            if (run != null)
            {
                yield return run;
            }
        }

        private IEnumerable<Run> FindRuns(int maxRunLength)
        {
            if (_isOrdered)
            {
                return FindOrderedRuns(maxRunLength);
            }
            else
            {
                return FindUnorderedRuns(maxRunLength);
            }
        }

        private IEnumerable<Run> FindUnorderedRuns(int maxRunLength)
        {
            var runs = new List<Run>();

            var originalIndex = 0;
            foreach (var request in _requests)
            {
                var run = runs.FirstOrDefault(r => r.RequestType == request.RequestType);

                if (run == null)
                {
                    run = new Run();
                    runs.Add(run);
                }
                else if (run.Count == maxRunLength)
                {
                    yield return run;
                    runs.Remove(run);
                    run = new Run();
                    runs.Add(run);
                }

                run.Add(request, originalIndex);
                originalIndex++;
            }

            foreach (var run in runs)
            {
                yield return run;
            }
        }

        // nested types
        private class Run
        {
            // fields
            private IndexMap _indexMap = new IndexMap.RangeBased();
            private readonly List<WriteRequest> _requests = new List<WriteRequest>();

            // properties
            public int Count
            {
                get { return _requests.Count; }
            }

            public IndexMap IndexMap
            {
                get { return _indexMap; }
            }

            public List<WriteRequest> Requests
            {
                get { return _requests; }
            }

            public WriteRequestType RequestType
            {
                get { return _requests[0].RequestType; }
            }

            // methods
            public void Add(WriteRequest request, int originalIndex)
            {
                var index = _requests.Count;
                _indexMap = _indexMap.Add(index, originalIndex);
                _requests.Add(request);
            }
        }
    }
}
