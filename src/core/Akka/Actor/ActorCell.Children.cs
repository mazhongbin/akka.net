﻿//-----------------------------------------------------------------------
// <copyright file="ActorCell.Children.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2018 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2018 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Akka.Actor.Internal;
using Akka.Serialization;
using Akka.Util;
using Akka.Util.Internal;

namespace Akka.Actor
{
    public partial class ActorCell
    {
        private volatile IChildrenContainer _childrenContainerDoNotCallMeDirectly = EmptyChildrenContainer.Instance;
        private long _nextRandomNameDoNotCallMeDirectly = -1; // Interlocked.Increment automatically adds 1 to this value. Allows us to start from 0.
        private ImmutableDictionary<string, FunctionRef> _functionRefsDoNotCallMeDirectly = ImmutableDictionary<string, FunctionRef>.Empty;

        /// <summary>
        /// The child container collection, used to house information about all child actors.
        /// </summary>
        public IChildrenContainer ChildrenContainer => _childrenContainerDoNotCallMeDirectly;

        private IReadOnlyCollection<IActorRef> Children => ChildrenContainer.Children;
        
        private ImmutableDictionary<string, FunctionRef> FunctionRefs => Volatile.Read(ref _functionRefsDoNotCallMeDirectly);

        internal bool TryGetFunctionRef(string name, out FunctionRef functionRef) => 
            FunctionRefs.TryGetValue(name, out functionRef);

        internal bool TryGetFunctionRef(string name, int uid, out FunctionRef functionRef) => 
            FunctionRefs.TryGetValue(name, out functionRef) && (uid == ActorCell.UndefinedUid || uid == functionRef.Path.Uid);

        internal FunctionRef AddFunctionRef(Action<IActorRef, object> tell, string suffix = "")
        {
            var r = GetRandomActorName("$$");
            var n = string.IsNullOrEmpty(suffix) ? r : r + "-" + suffix;
            var childPath = new ChildActorPath(Self.Path, n, NewUid());
            var functionRef = new FunctionRef(childPath, SystemImpl.Provider, SystemImpl.EventStream, tell);

            return ImmutableInterlocked.GetOrAdd(ref _functionRefsDoNotCallMeDirectly, childPath.Name, functionRef);
        }

        internal bool RemoveFunctionRef(FunctionRef functionRef)
        {
            if (functionRef.Path.Parent != Self.Path) throw new InvalidOperationException($"Trying to remove FunctionRef {functionRef.Path} from wrong ActorCell");

            var name = functionRef.Path.Name;
            if (ImmutableInterlocked.TryRemove(ref _functionRefsDoNotCallMeDirectly, name, out var fref))
            {
                fref.Stop();
                return true;
            }
            else return false;
        }

        protected void StopFunctionRefs()
        {
            var refs = Interlocked.Exchange(ref _functionRefsDoNotCallMeDirectly, ImmutableDictionary<string, FunctionRef>.Empty);
            foreach (var pair in refs)
            {
                pair.Value.Stop();
            }
        }

        /// <summary>
        /// Attaches a child to the current <see cref="ActorCell"/>.
        /// 
        /// This method is used in the process of starting actors.
        /// </summary>
        /// <param name="props">The <see cref="Props"/> this child actor will use.</param>
        /// <param name="isSystemService">If <c>true</c>, then this actor is a system actor and activates a special initialization path.</param>
        /// <param name="name">The name of the actor being started. Can be <c>null</c>, and if it is we will automatically 
        /// generate a random name for this actor.</param>
        /// <exception cref="InvalidActorNameException">
        /// This exception is thrown if the given <paramref name="name"/> is an invalid actor name.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// This exception is thrown if a pre-creation serialization occurred.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// This exception is thrown if the actor tries to create a child while it is terminating or is terminated.
        /// </exception>
        /// <returns>A reference to the initialized child actor.</returns>
        public virtual IActorRef AttachChild(Props props, bool isSystemService, string name = null)
        {
            return MakeChild(props, name == null ? GetRandomActorName() : CheckName(name), true, isSystemService);
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="props">TBD</param>
        /// <param name="name">TBD</param>
        /// <exception cref="InvalidActorNameException">
        /// This exception is thrown if the given <paramref name="name"/> is an invalid actor name.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// This exception is thrown if a pre-creation serialization occurred.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// This exception is thrown if the actor tries to create a child while it is terminating or is terminated.
        /// </exception>
        /// <returns>TBD</returns>
        public virtual IActorRef ActorOf(Props props, string name = null)
        {
            return ActorOf(props, name, false, false);
        }

        private IActorRef ActorOf(Props props, string name, bool isAsync, bool isSystemService)
        {
            if (name == null)
                name = GetRandomActorName();
            else
                CheckName(name);

            return MakeChild(props, name, isAsync, isSystemService);
        }
        
        private string GetRandomActorName(string prefix = "$")
        {
            var id = Interlocked.Increment(ref _nextRandomNameDoNotCallMeDirectly);
            var sb = new StringBuilder(prefix);
            return id.Base64Encode(sb).ToString();
        }

        /// <summary>
        ///     Stops the specified child.
        /// </summary>
        /// <param name="child">The child.</param>
        public void Stop(IActorRef child)
        {
            ChildRestartStats stats;
            if (ChildrenContainer.TryGetByRef(child, out stats))
            {
                var repointableActorRef = child as RepointableActorRef;
                if (repointableActorRef == null || repointableActorRef.IsStarted)
                {
                    UpdateChildrenRefs(c => c.ShallDie(child));
                }
            }
            ((IInternalActorRef)child).Stop();
        }

        /// <summary>
        /// Swaps out the children container, by calling <paramref name="updater"/>  to produce the new container.
        /// If the underlying container has been updated while <paramref name="updater"/> was called,
        /// <paramref name="updater"/> will be called again with the new container. This will repeat until the 
        /// container can be swapped out, or until <see cref="Tuple{T1,T2,T3}.Item1"/> contains <c>false</c>.
        /// <para>The returned tuple should contain:</para>
        /// <para>Item1: <c>true</c> if the container should be updated; <c>false</c> to not update and return Item3</para>
        /// <para>Item2: The new container (will only be used if Item1=<c>true</c>)</para>
        /// <para>Item3: The return value</para>
        /// </summary>
        /// <param name="updater">A function that returns a new container.</param>
        /// <returns>The third value of the tuple that <paramref name="updater"/> returned.</returns>
        private TReturn UpdateChildrenRefs<TReturn>(Func<IChildrenContainer, Tuple<bool, IChildrenContainer, TReturn>> updater)
        {
            while (true)
            {
                var current = ChildrenContainer;
                var t = updater(current);
                if (!t.Item1) return t.Item3;
                if (Interlocked.CompareExchange(ref _childrenContainerDoNotCallMeDirectly, t.Item2, current) == current) return t.Item3;
            }
        }

        /// <summary>
        /// Swaps out the children container, by calling <paramref name="updater" />  to produce the new container.
        /// If the underlying container has been updated while <paramref name="updater" /> was called,
        /// <paramref name="updater" /> will be called again with the new container. This will repeat until the
        /// container can be swapped out.
        /// </summary>
        /// <param name="updater">A function that returns a new container.</param>
        /// <returns>The new updated <see cref="ChildrenContainer"/></returns>
        private IChildrenContainer UpdateChildrenRefs(Func<IChildrenContainer, IChildrenContainer> updater)
        {
            return InterlockedSpin.Swap(ref _childrenContainerDoNotCallMeDirectly, updater);
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="name">TBD</param>
        public void ReserveChild(string name)
        {
            UpdateChildrenRefs(c => c.Reserve(name));
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="name">TBD</param>
        protected void UnreserveChild(string name)
        {
            UpdateChildrenRefs(c => c.Unreserve(name));

        }

        /// <summary>
        /// This should only be used privately or when creating the root actor. 
        /// </summary>
        /// <param name="actor">TBD</param>
        /// <returns>TBD</returns>
        public ChildRestartStats InitChild(IInternalActorRef actor)
        {
            return UpdateChildrenRefs(cc =>
            {
                IChildStats stats;
                var name = actor.Path.Name;
                if (cc.TryGetByName(name, out stats))
                {
                    var old = stats as ChildRestartStats;
                    if (old != null)
                    {
                        //Do not update. Return old
                        return new Tuple<bool, IChildrenContainer, ChildRestartStats>(false, cc, old);
                    }
                    if (stats is ChildNameReserved)
                    {
                        var crs = new ChildRestartStats(actor);
                        var updatedContainer = cc.Add(name, crs);
                        //Update (if it's still cc) and return the new crs
                        return new Tuple<bool, IChildrenContainer, ChildRestartStats>(true, updatedContainer, crs);
                    }
                }
                //Do not update. Return null
                return new Tuple<bool, IChildrenContainer, ChildRestartStats>(false, cc, null);
            });
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="reason">TBD</param>
        /// <returns>TBD</returns>
        protected bool SetChildrenTerminationReason(SuspendReason reason)
        {
            return UpdateChildrenRefs(cc =>
            {
                var c = cc as TerminatingChildrenContainer;
                if (c != null)
                    //The arguments says: Update; with a new reason; and return true
                    return new Tuple<bool, IChildrenContainer, bool>(true, c.CreateCopyWithReason(reason), true);
                
                //The arguments says:Do NOT update; any container will do since it wont be updated; return false 
                return new Tuple<bool, IChildrenContainer, bool>(false, cc, false);
            });
        }

        /// <summary>
        /// TBD
        /// </summary>
        protected void SetTerminated()
        {
            UpdateChildrenRefs(c => TerminatedChildrenContainer.Instance);
        }

        /// <summary>
        /// TBD
        /// </summary>
        protected bool IsNormal { get { return ChildrenContainer.IsNormal; } }
        /// <summary>
        /// TBD
        /// </summary>
        protected bool IsTerminating { get { return ChildrenContainer.IsTerminating; } }

        private bool IsWaitingForChildren  // This is called isWaitingForChildrenOrNull in AkkaJVM but is used like if returned a bool
        {
            get
            {
                var terminating = ChildrenContainer as TerminatingChildrenContainer;
                return terminating != null && terminating.Reason is SuspendReason.IWaitingForChildren;
            }
        }

        /// <summary>
        ///     Suspends the children.
        /// </summary>
        private void SuspendChildren(List<IActorRef> exceptFor = null)
        {
            if (exceptFor == null)
            {
                foreach (var stats in ChildrenContainer.Stats)
                {
                    var child = stats.Child;
                    child.Suspend();
                }
            }
            else
            {
                foreach (var stats in ChildrenContainer.Stats)
                {
                    var child = stats.Child;
                    if (!exceptFor.Contains(child))
                        child.Suspend();
                }
            }
        }

        /// <summary>
        ///     Resumes the children.
        /// </summary>
        private void ResumeChildren(Exception causedByFailure, IActorRef perpetrator)
        {
            foreach (var stats in ChildrenContainer.Stats)
            {
                var child = stats.Child;
                var cause = (perpetrator != null && child.Equals(perpetrator)) ? causedByFailure : null;
                child.Resume(cause);
            }
        }

        /// <summary>
        /// Tries to get the stats for the child with the specified name. The stats can be either <see cref="ChildNameReserved"/> 
        /// indicating that only a name has been reserved for the child, or a <see cref="ChildRestartStats"/> for a child that 
        /// has been initialized/created.
        /// </summary>
        /// <param name="name">TBD</param>
        /// <param name="child">TBD</param>
        /// <returns>TBD</returns>
        public bool TryGetChildStatsByName(string name, out IChildStats child)   //This is called getChildByName in Akka JVM
        {
            return ChildrenContainer.TryGetByName(name, out child);
        }

        /// <summary>
        /// Tries to get the stats for the child with the specified name. This ignores children for whom only names have been reserved.
        /// </summary>
        private bool TryGetChildRestartStatsByName(string name, out ChildRestartStats child)
        {
            IChildStats stats;
            if (ChildrenContainer.TryGetByName(name, out stats))
            {
                child = stats as ChildRestartStats;
                if (child != null)
                    return true;
            }
            child = null;
            return false;
        }

        /// <summary>
        /// Tries to get the stats for the specified child.
        /// <remarks>Since the child exists <see cref="ChildRestartStats"/> is the only valid <see cref="IChildStats"/>.</remarks>
        /// </summary>
        /// <param name="actor">TBD</param>
        /// <param name="child">TBD</param>
        /// <returns>TBD</returns>
        protected bool TryGetChildStatsByRef(IActorRef actor, out ChildRestartStats child)   //This is called getChildByRef in Akka JVM
        {
            return ChildrenContainer.TryGetByRef(actor, out child);
        }

        // In Akka JVM there is a getAllChildStats here. Use ChildrenRefs.Stats instead

        /// <summary>
        /// Obsolete. Use <see cref="TryGetSingleChild(string, out IInternalActorRef)"/> instead.
        /// </summary>
        /// <param name="name">N/A</param>
        /// <returns>N/A</returns>
        [Obsolete("Use TryGetSingleChild [0.7.1]")]
        public IInternalActorRef GetSingleChild(string name)
        {
            return TryGetSingleChild(name, out var child) ? child : ActorRefs.Nobody;
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="name">TBD</param>
        /// <param name="child">TBD</param>
        /// <returns>TBD</returns>
        public bool TryGetSingleChild(string name, out IInternalActorRef child)
        {
            if (name.IndexOf('#') < 0)
            {
                // optimization for the non-uid case
                if (TryGetChildRestartStatsByName(name, out var stats))
                {
                    child = stats.Child;
                    return true;
                }
                else if (TryGetFunctionRef(name, out var functionRef))
                {
                    child = functionRef;
                    return true;
                }
            }
            else
            {
                var nameAndUid = SplitNameAndUid(name);
                if (TryGetChildRestartStatsByName(nameAndUid.Name, out var stats))
                {
                    var uid = nameAndUid.Uid;
                    if (uid == ActorCell.UndefinedUid || uid == stats.Uid)
                    {
                        child = stats.Child;
                        return true;
                    }
                }
                else if (TryGetFunctionRef(nameAndUid.Name, nameAndUid.Uid, out var functionRef))
                {
                    child = functionRef;
                    return true;
                }
            }
            child = ActorRefs.Nobody;
            return false;
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="child">TBD</param>
        /// <returns>TBD</returns>
        protected SuspendReason RemoveChildAndGetStateChange(IActorRef child)
        {
            if (ChildrenContainer is TerminatingChildrenContainer terminating)
            {
                var newContainer = UpdateChildrenRefs(c => c.Remove(child));
                if (newContainer is TerminatingChildrenContainer) return null;
                return terminating.Reason;
            }
            UpdateChildrenRefs(c => c.Remove(child));
            return null;
        }

        private static string CheckName(string name)
        {
            if (name == null) throw new InvalidActorNameException("Actor name must not be null.");
            if (name.Length == 0) throw new InvalidActorNameException("Actor name must not be empty.");
            if (!ActorPath.IsValidPathElement(name))
            {
                throw new InvalidActorNameException($"Illegal actor name [{name}]. Actor paths MUST: not start with `$`, include only ASCII letters and can only contain these special characters: ${new string(ActorPath.ValidSymbols)}.");
            }
            return name;
        }

        private IInternalActorRef MakeChild(Props props, string name, bool async, bool systemService)
        {
            if (_systemImpl.Settings.SerializeAllCreators && !systemService && !(props.Deploy.Scope is LocalScope))
            {
                var ser = _systemImpl.Serialization;
                if (props.Arguments != null)
                {
                    foreach (var argument in props.Arguments)
                    {
                        if (argument != null && !(argument is INoSerializationVerificationNeeded))
                        {
                            var serializer = ser.FindSerializerFor(argument);
                            var bytes = serializer.ToBinary(argument);
                            var manifestSerializer = serializer as SerializerWithStringManifest;
                            if (manifestSerializer != null)
                            {
                                var manifest = manifestSerializer.Manifest(argument);
                                if (ser.Deserialize(bytes, manifestSerializer.Identifier, manifest) == null)
                                {
                                    throw new ArgumentException($"Pre-creation serialization check failed at [${_self.Path}/{name}]", nameof(name));
                                }
                            }
                            else
                            {
                                if (ser.Deserialize(bytes, serializer.Identifier, argument.GetType().TypeQualifiedName()) == null)
                                {
                                    throw new ArgumentException($"Pre-creation serialization check failed at [${_self.Path}/{name}]", nameof(name));
                                }
                            }
                        }
                    }
                }
            }

            // In case we are currently terminating, fail external attachChild requests
            // (internal calls cannot happen anyway because we are suspended)
            if (ChildrenContainer.IsTerminating)
            {
                throw new InvalidOperationException("Cannot create child while terminating or terminated");
            }
            else
            {
                // this name will either be unreserved or overwritten with a real child below
                ReserveChild(name);
                IInternalActorRef actor;
                try
                {
                    var childPath = new ChildActorPath(Self.Path, name, NewUid());
                    actor = _systemImpl.Provider.ActorOf(_systemImpl, props, _self, childPath,
                        systemService: systemService, deploy: null, lookupDeploy: true, async: async);
                }
                catch
                {
                    //if actor creation failed, unreserve the name
                    UnreserveChild(name);
                    throw;
                }

                if (Mailbox != null && IsFailed)
                {
                    for(var i = 1; i <= Mailbox.SuspendCount(); i++)
                        actor.Suspend();
                }

                //replace the reservation with the real actor
                InitChild(actor);
                actor.Start();
                return actor;
            }
        }
    }
}

