﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

#pragma warning disable 0162

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace FASTER.core
{
    public partial class FasterKV<Key, Value, Input, Output, Context, Functions> : FasterBase, IFasterKV<Key, Value, Input, Output, Context>
        where Key : new()
        where Value : new()
        where Functions : IFunctions<Key, Value, Input, Output, Context>
    {
        private readonly Functions functions;
        private readonly AllocatorBase<Key, Value> hlog;
        private readonly AllocatorBase<Key, Value> readcache;
        private readonly IFasterEqualityComparer<Key> comparer;

        private readonly bool UseReadCache = false;
        private readonly bool CopyReadsToTail = false;
        private readonly bool FoldOverSnapshot = false;
        private readonly int sectorSize;
        private readonly bool WriteDefaultOnDelete = false;
        private bool RelaxedCPR = false;

        /// <summary>
        /// Use relaxed version of CPR, where ops pending I/O
        /// are not part of CPR checkpoint. This mode allows
        /// us to eliminate the WAIT_PENDING phase, and allows
        /// sessions to be suspended. Do not modify during checkpointing.
        /// </summary>
        public void UseRelaxedCPR() => RelaxedCPR = true;

        /// <summary>
        /// Number of used entries in hash index
        /// </summary>
        public long EntryCount => GetEntryCount();

        /// <summary>
        /// Size of index in #cache lines (64 bytes each)
        /// </summary>
        public long IndexSize => state[resizeInfo.version].size;

        /// <summary>
        /// Comparer used by FASTER
        /// </summary>
        public IFasterEqualityComparer<Key> Comparer => comparer;

        /// <summary>
        /// Hybrid log used by this FASTER instance
        /// </summary>
        public LogAccessor<Key, Value, Input, Output, Context> Log { get; }

        /// <summary>
        /// Read cache used by this FASTER instance
        /// </summary>
        public LogAccessor<Key, Value, Input, Output, Context> ReadCache { get; }

        private enum CheckpointType
        {
            INDEX_ONLY,
            HYBRID_LOG_ONLY,
            FULL
        }

        private CheckpointType _checkpointType;
        private Guid _indexCheckpointToken;
        private Guid _hybridLogCheckpointToken;
        private SystemState _systemState;

        private HybridLogCheckpointInfo _hybridLogCheckpoint;


        private ConcurrentDictionary<Guid, CommitPoint> _recoveredSessions;

        private readonly FastThreadLocal<FasterExecutionContext> threadCtx;


        /// <summary>
        /// Create FASTER instance
        /// </summary>
        /// <param name="size">Size of core index (#cache lines)</param>
        /// <param name="comparer">FASTER equality comparer for key</param>
        /// <param name="variableLengthStructSettings"></param>
        /// <param name="functions">Callback functions</param>
        /// <param name="logSettings">Log settings</param>
        /// <param name="checkpointSettings">Checkpoint settings</param>
        /// <param name="serializerSettings">Serializer settings</param>
        public FasterKV(long size, Functions functions, LogSettings logSettings, CheckpointSettings checkpointSettings = null, SerializerSettings<Key, Value> serializerSettings = null, IFasterEqualityComparer<Key> comparer = null, VariableLengthStructSettings<Key, Value> variableLengthStructSettings = null)
        {
            threadCtx = new FastThreadLocal<FasterExecutionContext>();

            if (comparer != null)
                this.comparer = comparer;
            else
            {
                if (typeof(IFasterEqualityComparer<Key>).IsAssignableFrom(typeof(Key)))
                {
                    this.comparer = new Key() as IFasterEqualityComparer<Key>;
                }
                else
                {
                    Console.WriteLine("***WARNING*** Creating default FASTER key equality comparer based on potentially slow EqualityComparer<Key>.Default. To avoid this, provide a comparer (IFasterEqualityComparer<Key>) as an argument to FASTER's constructor, or make Key implement the interface IFasterEqualityComparer<Key>");
                    this.comparer = FasterEqualityComparer<Key>.Default;
                }
            }

            if (checkpointSettings == null)
                checkpointSettings = new CheckpointSettings();

            if (checkpointSettings.CheckpointDir != null && checkpointSettings.CheckpointManager != null)
                throw new Exception("Specify either CheckpointManager or CheckpointDir for CheckpointSettings, not both");

            checkpointManager = checkpointSettings.CheckpointManager ?? new LocalCheckpointManager(checkpointSettings.CheckpointDir ?? "");

            FoldOverSnapshot = checkpointSettings.CheckPointType == core.CheckpointType.FoldOver;
            CopyReadsToTail = logSettings.CopyReadsToTail;
            this.functions = functions;

            if (logSettings.ReadCacheSettings != null)
            {
                CopyReadsToTail = false;
                UseReadCache = true;
            }

            if (Utility.IsBlittable<Key>() && Utility.IsBlittable<Value>())
            {
                if (variableLengthStructSettings != null)
                {
                    hlog = new VariableLengthBlittableAllocator<Key, Value>(logSettings, variableLengthStructSettings, this.comparer, null, epoch);
                    Log = new LogAccessor<Key, Value, Input, Output, Context>(this, hlog);
                    if (UseReadCache)
                    {
                        readcache = new VariableLengthBlittableAllocator<Key, Value>(
                            new LogSettings
                            {
                                PageSizeBits = logSettings.ReadCacheSettings.PageSizeBits,
                                MemorySizeBits = logSettings.ReadCacheSettings.MemorySizeBits,
                                SegmentSizeBits = logSettings.ReadCacheSettings.MemorySizeBits,
                                MutableFraction = logSettings.ReadCacheSettings.SecondChanceFraction
                            }, variableLengthStructSettings, this.comparer, ReadCacheEvict, epoch);
                        readcache.Initialize();
                        ReadCache = new LogAccessor<Key, Value, Input, Output, Context>(this, readcache);
                    }
                }
                else
                {
                    hlog = new BlittableAllocator<Key, Value>(logSettings, this.comparer, null, epoch);
                    Log = new LogAccessor<Key, Value, Input, Output, Context>(this, hlog);
                    if (UseReadCache)
                    {
                        readcache = new BlittableAllocator<Key, Value>(
                            new LogSettings
                            {
                                PageSizeBits = logSettings.ReadCacheSettings.PageSizeBits,
                                MemorySizeBits = logSettings.ReadCacheSettings.MemorySizeBits,
                                SegmentSizeBits = logSettings.ReadCacheSettings.MemorySizeBits,
                                MutableFraction = logSettings.ReadCacheSettings.SecondChanceFraction
                            }, this.comparer, ReadCacheEvict, epoch);
                        readcache.Initialize();
                        ReadCache = new LogAccessor<Key, Value, Input, Output, Context>(this, readcache);
                    }
                }
            }
            else
            {
                WriteDefaultOnDelete = true;

                hlog = new GenericAllocator<Key, Value>(logSettings, serializerSettings, this.comparer, null, epoch);
                Log = new LogAccessor<Key, Value, Input, Output, Context>(this, hlog);
                if (UseReadCache)
                {
                    readcache = new GenericAllocator<Key, Value>(
                        new LogSettings
                        {
                            PageSizeBits = logSettings.ReadCacheSettings.PageSizeBits,
                            MemorySizeBits = logSettings.ReadCacheSettings.MemorySizeBits,
                            SegmentSizeBits = logSettings.ReadCacheSettings.MemorySizeBits,
                            MutableFraction = logSettings.ReadCacheSettings.SecondChanceFraction
                        }, serializerSettings, this.comparer, ReadCacheEvict, epoch);
                    readcache.Initialize();
                    ReadCache = new LogAccessor<Key, Value, Input, Output, Context>(this, readcache);
                }
            }

            hlog.Initialize();

            sectorSize = (int)logSettings.LogDevice.SectorSize;
            Initialize(size, sectorSize);

            _systemState = default(SystemState);
            _systemState.phase = Phase.REST;
            _systemState.version = 1;
            _checkpointType = CheckpointType.HYBRID_LOG_ONLY;
        }

        /// <summary>
        /// Take full checkpoint
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public bool TakeFullCheckpoint(out Guid token)
        {
            var success = InternalTakeCheckpoint(CheckpointType.FULL);
            if (success)
            {
                token = _indexCheckpointToken;
            }
            else
            {
                token = default(Guid);
            }
            return success;
        }

        /// <summary>
        /// Take index checkpoint
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public bool TakeIndexCheckpoint(out Guid token)
        {
            var success = InternalTakeCheckpoint(CheckpointType.INDEX_ONLY);
            if (success)
            {
                token = _indexCheckpointToken;
            }
            else
            {
                token = default(Guid);
            }
            return success;
        }

        /// <summary>
        /// Take hybrid log checkpoint
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public bool TakeHybridLogCheckpoint(out Guid token)
        {
            var success = InternalTakeCheckpoint(CheckpointType.HYBRID_LOG_ONLY);
            if (success)
            {
                token = _hybridLogCheckpointToken;
            }
            else
            {
                token = default(Guid);
            }
            return success;
        }

        /// <summary>
        /// Recover from the latest checkpoints
        /// </summary>
        public void Recover()
        {
            InternalRecoverFromLatestCheckpoints();
        }

        /// <summary>
        /// Recover
        /// </summary>
        /// <param name="fullCheckpointToken"></param>
        public void Recover(Guid fullCheckpointToken)
        {
            InternalRecover(fullCheckpointToken, fullCheckpointToken);
        }

        /// <summary>
        /// Recover
        /// </summary>
        /// <param name="indexCheckpointToken"></param>
        /// <param name="hybridLogCheckpointToken"></param>
        public void Recover(Guid indexCheckpointToken, Guid hybridLogCheckpointToken)
        {
            InternalRecover(indexCheckpointToken, hybridLogCheckpointToken);
        }

        /// <summary>
        /// Start session with FASTER - call once per thread before using FASTER
        /// </summary>
        /// <returns></returns>
        public Guid StartSession()
        {
            return InternalAcquire();
        }

        /// <summary>
        /// Continue session with FASTER
        /// </summary>
        /// <param name="guid"></param>
        /// <returns></returns>
        public CommitPoint ContinueSession(Guid guid)
        {
            StartSession();

            var cp = InternalContinue(guid, out FasterExecutionContext ctx);
            threadCtx.Value = ctx;

            return cp;
        }

        /// <summary>
        /// Stop session with FASTER
        /// </summary>
        public void StopSession()
        {
            InternalRelease(this.threadCtx.Value);
        }

        /// <summary>
        /// Refresh epoch (release memory pins)
        /// </summary>
        public void Refresh()
        {
            InternalRefresh(threadCtx.Value);
        }


        /// <summary>
        /// Complete all pending operations issued by this session
        /// </summary>
        /// <param name="wait">Whether we spin-wait for pending operations to complete</param>
        /// <returns>Whether all pending operations have completed</returns>
        public bool CompletePending(bool wait = false)
        {
            return InternalCompletePending(threadCtx.Value, wait);
        }

        /// <summary>
        /// Get list of pending requests (for local session)
        /// </summary>
        /// <returns></returns>
        public IEnumerable<long> GetPendingRequests()
        {

            foreach (var kvp in threadCtx.Value.prevCtx?.ioPendingRequests)
                yield return kvp.Value.serialNum;

            foreach (var val in threadCtx.Value.prevCtx?.retryRequests)
                yield return val.serialNum;

            foreach (var kvp in threadCtx.Value.ioPendingRequests)
                yield return kvp.Value.serialNum;

            foreach (var val in threadCtx.Value.retryRequests)
                yield return val.serialNum;
        }

        /// <summary>
        /// Complete the ongoing checkpoint (if any)
        /// </summary>
        /// <param name="wait">Spin-wait for completion</param>
        /// <returns></returns>
        public bool CompleteCheckpoint(bool wait = false)
        {
            if (threadCtx == null)
            {
                // the thread does not have an active session
                // we can wait until system state becomes REST
                do
                {
                    if (_systemState.phase == Phase.REST)
                    {
                        return true;
                    }
                } while (wait);
            }
            else
            {
                // the thread does has an active session and 
                // so we need to constantly complete pending 
                // and refresh (done inside CompletePending)
                // for the checkpoint to be proceed
                do
                {
                    CompletePending();
                    if (_systemState.phase == Phase.REST)
                    {
                        CompletePending();
                        return true;
                    }
                } while (wait);
            }
            return false;
        }

        /// <summary>
        /// Read operation
        /// </summary>
        /// <param name="key">Key of read</param>
        /// <param name="input">Input argument used by Reader to select what part of value to read</param>
        /// <param name="output">Reader stores the read result in output</param>
        /// <param name="context">User context to identify operation in asynchronous callback</param>
        /// <param name="serialNo">Increasing sequence number of operation (used for recovery)</param>
        /// <returns>Status of operation</returns>
        public Status Read(ref Key key, ref Input input, ref Output output, Context context, long serialNo)
        {
            return Read(ref key, ref input, ref output, context, serialNo, threadCtx.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Status Read(ref Key key, ref Input input, ref Output output, Context context, long serialNo, FasterExecutionContext sessionCtx)
        {
            var pcontext = default(PendingContext);
            var internalStatus = InternalRead(ref key, ref input, ref output, ref context, ref pcontext, sessionCtx);
            Status status;
            if (internalStatus == OperationStatus.SUCCESS || internalStatus == OperationStatus.NOTFOUND)
            {
                status = (Status)internalStatus;
            }
            else
            {
                status = HandleOperationStatus(sessionCtx, sessionCtx, pcontext, internalStatus);
            }
            sessionCtx.serialNum = serialNo;
            return status;
        }

        /// <summary>
        /// (Blind) upsert operation
        /// </summary>
        /// <param name="key">Key of read</param>
        /// <param name="value">Value being upserted</param>
        /// <param name="context">User context to identify operation in asynchronous callback</param>
        /// <param name="serialNo">Increasing sequence number of operation (used for recovery)</param>
        /// <returns>Status of operation</returns>
        public Status Upsert(ref Key key, ref Value value, Context context, long serialNo)
        {
            return Upsert(ref key, ref value, context, serialNo, threadCtx.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Status Upsert(ref Key key, ref Value value, Context context, long serialNo, FasterExecutionContext sessionCtx)
        {
            var pcontext = default(PendingContext);
            var internalStatus = InternalUpsert(ref key, ref value, ref context, ref pcontext, sessionCtx);
            Status status;

            if (internalStatus == OperationStatus.SUCCESS || internalStatus == OperationStatus.NOTFOUND)
            {
                status = (Status)internalStatus;
            }
            else
            {
                status = HandleOperationStatus(sessionCtx, sessionCtx, pcontext, internalStatus);
            }
            sessionCtx.serialNum = serialNo;
            return status;
        }


        /// <summary>
        /// Atomic read-modify-write operation
        /// </summary>
        /// <param name="key">Key of read</param>
        /// <param name="input">Input argument used by RMW callback to perform operation</param>
        /// <param name="context">User context to identify operation in asynchronous callback</param>
        /// <param name="serialNo">Increasing sequence number of operation (used for recovery)</param>
        /// <returns>Status of operation</returns>
        public Status RMW(ref Key key, ref Input input, Context context, long serialNo)
        {
            return RMW(ref key, ref input, context, serialNo, threadCtx.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Status RMW(ref Key key, ref Input input, Context context, long serialNo, FasterExecutionContext sessionCtx)
        {
            var pcontext = default(PendingContext);
            var internalStatus = InternalRMW(ref key, ref input, ref context, ref pcontext, sessionCtx);
            Status status;
            if (internalStatus == OperationStatus.SUCCESS || internalStatus == OperationStatus.NOTFOUND)
            {
                status = (Status)internalStatus;
            }
            else
            {
                status = HandleOperationStatus(sessionCtx, sessionCtx, pcontext, internalStatus);
            }
            sessionCtx.serialNum = serialNo;
            return status;
        }

        /// <summary>
        /// Delete entry (use tombstone if necessary)
        /// Hash entry is removed as a best effort (if key is in memory and at 
        /// the head of hash chain.
        /// Value is set to null (using ConcurrentWrite) if it is in mutable region
        /// </summary>
        /// <param name="key">Key of delete</param>
        /// <param name="context">User context to identify operation in asynchronous callback</param>
        /// <param name="serialNo">Increasing sequence number of operation (used for recovery)</param>
        /// <returns>Status of operation</returns>
        public Status Delete(ref Key key, Context context, long serialNo)
        {
            return Delete(ref key, context, serialNo, threadCtx.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Status Delete(ref Key key, Context context, long serialNo, FasterExecutionContext sessionCtx)
        {
            var pcontext = default(PendingContext);
            var internalStatus = InternalDelete(ref key, ref context, ref pcontext, sessionCtx);
            var status = default(Status);
            if (internalStatus == OperationStatus.SUCCESS || internalStatus == OperationStatus.NOTFOUND)
            {
                status = (Status)internalStatus;
            }
            sessionCtx.serialNum = serialNo;
            return status;
        }

        /// <summary>
        /// Experimental feature
        /// Checks whether specified record is present in memory
        /// (between HeadAddress and tail, or between fromAddress
        /// and tail)
        /// </summary>
        /// <param name="key">Key of the record.</param>
        /// <param name="fromAddress">Look until this address</param>
        /// <returns>Status</returns>
        public Status ContainsKeyInMemory(ref Key key, long fromAddress = -1)
        {
            return InternalContainsKeyInMemory(ref key, threadCtx.Value, fromAddress);
        }

        /// <summary>
        /// Grow the hash index
        /// </summary>
        /// <returns>Whether the request succeeded</returns>
        public bool GrowIndex()
        {
            return InternalGrowIndex();
        }

        /// <summary>
        /// Dispose FASTER instance
        /// </summary>
        public void Dispose()
        {
            base.Free();
            threadCtx?.Dispose();
            hlog.Dispose();
            readcache?.Dispose();
        }
    }
}
