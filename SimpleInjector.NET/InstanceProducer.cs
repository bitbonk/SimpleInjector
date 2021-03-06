﻿#region Copyright Simple Injector Contributors
/* The Simple Injector is an easy-to-use Inversion of Control library for .NET
 * 
 * Copyright (c) 2013 - 2014 Simple Injector Contributors
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
 * associated documentation files (the "Software"), to deal in the Software without restriction, including 
 * without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
 * copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the 
 * following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all copies or substantial 
 * portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
 * LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO 
 * EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER 
 * IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE 
 * USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
#endregion

namespace SimpleInjector
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Runtime.CompilerServices;
    using SimpleInjector.Advanced;
    using SimpleInjector.Diagnostics;
    using SimpleInjector.Diagnostics.Analyzers;
    using SimpleInjector.Internals;
    using SimpleInjector.Lifestyles;

    /// <summary>
    /// Produces instances for a given registration. Instances of this type are generally created by the
    /// container when calling one of the <b>Register</b> overloads. Instances can be retrieved by calling
    /// <see cref="Container.GetCurrentRegistrations()"/> or <see cref="Container.GetRegistration(Type, bool)"/>.
    /// </summary>
    /// <remarks>
    /// The <b>Register</b> method overloads create <b>InstanceProducer</b> instances internally, but
    /// <b>InstanceProducer</b>s can be created manually to implement special scenarios. An 
    /// <b>InstanceProducer</b> wraps <see cref="Registration"/> instance. The <b>Registration</b> builds an
    /// <see cref="Expression"/> that describes the intend to create the instance according to a certain
    /// lifestyle. The <b>InstanceProducer</b> on the other hand transforms this <b>Expression</b> to a
    /// delegate and allows the actual instance to be created. A <b>Registration</b> itself can't create any
    /// instance. The <b>InsanceProducer</b> allows intercepting created instances by hooking onto the
    /// <see cref="SimpleInjector.Container.ExpressionBuilt">Container.ExpressionBuilt</see> event. The
    /// <see cref="Container.RegisterDecorator(Type, Type)">RegisterDecorator</see> methods for instance work 
    /// by hooking onto the <b>ExpressionBuilt</b> event and allow wrapping the returned instance with a decorator.
    /// </remarks>
    /// <example>
    /// The following example shows the creation of two different <b>InstanceProducer</b> instances that wrap
    /// the same <b>Registration</b> instance. Since the <b>Registration</b> is created using the 
    /// <see cref="SimpleInjector.Lifestyle.Singleton">Singleton</see> lifestyle, both producers will return 
    /// the same instance. The <b>InstanceProducer</b> for the <code>Interface1</code> however, will wrap that
    /// instance in a (transient) <code>Interface1Decorator</code>.
    /// <code lang="cs"><![CDATA[
    /// var container = new Container();
    /// 
    /// // ServiceImpl implements both Interface1 and Interface2.
    /// var registration = Lifestyle.Singleton.CreateRegistration<ServiceImpl, ServiceImpl>(container);
    /// 
    /// var producer1 = new InstanceProducer(typeof(Interface1), registration);
    /// var producer2 = new InstanceProducer(typeof(Interface2), registration);
    /// 
    /// container.RegisterDecorator(typeof(Interface1), typeof(Interface1Decorator));
    /// 
    /// var instance1 = (Interface1)producer1.GetInstance();
    /// var instance2 = (Interface2)producer2.GetInstance();
    /// 
    /// Assert.IsInstanceOfType(instance1, typeof(Interface1Decorator));
    /// Assert.IsInstanceOfType(instance2, typeof(ServiceImpl));
    /// 
    /// Assert.AreSame(((Interface1Decorator)instance1).DecoratedInstance, instance2);
    /// ]]></code>
    /// </example>
    [DebuggerTypeProxy(typeof(InstanceProducerDebugView))]
    [DebuggerDisplay("{DebuggerDisplay,nq}")]
    public class InstanceProducer
    {
        private static readonly Action[] NoVerifiers = Helpers.Array<Action>.Empty;

        private readonly object locker = new object();
        private readonly Lazy<Expression> lazyExpression;
        private readonly InitializationContext initializationContext;

        private CyclicDependencyValidator validator;
        private Func<object> instanceCreator;
        private bool? isValid = true;
        private Lifestyle overriddenLifestyle;
        private ReadOnlyCollection<KnownRelationship> knownRelationships;
        private List<Action> verifiers;

        /// <summary>Initializes a new instance of the <see cref="InstanceProducer"/> class.</summary>
        /// <param name="serviceType">The service type for which this instance is created.</param>
        /// <param name="registration">The <see cref="Registration"/>.</param>
        public InstanceProducer(Type serviceType, Registration registration)
            : this(serviceType, registration, ShouldBeRegisteredAsAnExternalProducer(registration))
        {
        }

        internal InstanceProducer(Type serviceType, Registration registration, bool registerExternalProducer)
        {
            Requires.IsNotNull(serviceType, "serviceType");
            Requires.IsNotNull(registration, "registration");

            this.ServiceType = serviceType;
            this.Registration = registration;
            this.validator = new CyclicDependencyValidator(registration.ImplementationType);

            this.lazyExpression = new Lazy<Expression>(this.BuildExpressionInternal);
            this.initializationContext = new InitializationContext(this, registration);

            if (registerExternalProducer)
            {
                registration.Container.RegisterExternalProducer(this);
            }
        }

        /// <summary>
        /// Gets the <see cref="Lifestyle"/> for this registration. The returned lifestyle can differ from the
        /// lifestyle that is used during the registration. This can happen for instance when the registration
        /// is changed by an <see cref="Container.ExpressionBuilt">ExpressionBuilt</see> registration or
        /// gets decorated.
        /// </summary>
        /// <value>The <see cref="Lifestyle"/> for this registration.</value>
        public Lifestyle Lifestyle
        {
            get { return this.overriddenLifestyle ?? this.Registration.Lifestyle; }
        }

        /// <summary>Gets the service type for which this producer produces instances.</summary>
        /// <value>A <see cref="Type"/> instance.</value>
        public Type ServiceType { get; private set; }

        /// <summary>Gets the <see cref="Registration"/> instance for this instance.</summary>
        /// <value>The <see cref="Registration"/>.</value>
        public Registration Registration { get; private set; }

        internal Type ImplementationType
        {
            get { return this.Registration.ImplementationType ?? this.ServiceType; }
        }

        internal InitializationContext InitializationContext
        {
            get { return this.initializationContext; }
        }

        // Flag that indicates that this type is created by the container (concrete or collection) or resolved
        // using unregistered type resolution.
        internal bool IsContainerAutoRegistered { get; set; }

        // Will only return false when the type is a concrete unregistered type that was automatically added
        // by the container, while the expression can not be generated.
        // Types that are registered upfront are always considered to be valid, while unregistered types must
        // be validated. The reason for this is that we must prevent the container to throw an exception when
        // GetRegistration() is called for an unregistered (concrete) type that can not be resolved.
        internal bool IsValid
        {
            get
            {
                if (this.isValid == null)
                {
                    this.Exception = this.GetExceptionIfInvalid();
                    this.isValid = this.Exception == null;
                }

                return this.isValid.GetValueOrDefault();
            }
        }

        // Gets set by the IsValid and indicates the reason why this producer is invalid. Will be null
        // when the producer is valid.
        internal Exception Exception { get; private set; }

        internal bool IsExpressionCreated
        {
            get { return this.lazyExpression.IsValueCreated && !this.Registration.MustBeVerified; }
        }

        internal bool MustBeExplicitlyVerified
        {
            get { return this.verifiers != null; }
        }

        internal bool InstanceSuccessfullyCreated { get; private set; }

        internal bool VerifiersAreSuccessfullyCalled { get; private set; }

        internal string DebuggerDisplay
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "ServiceType = {0}, Lifestyle = {1}",
                    this.ServiceType.ToFriendlyName(), this.Lifestyle.Name);
            }
        }

        /// <summary>Produces an instance.</summary>
        /// <returns>An instance. Will never return null.</returns>
        /// <exception cref="ActivationException">When the instance could not be retrieved or is null.</exception>
        [SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate", Justification =
            "A property is not appropriate, because get instance could possibly be a heavy operation.")]
        public object GetInstance()
        {
            this.validator.CheckForRecursiveCalls();

            object instance;

            try
            {
                if (this.instanceCreator == null)
                {
                    this.instanceCreator = this.BuildInstanceCreator();

                    instance = this.instanceCreator();

                    this.InstanceSuccessfullyCreated = true;
                }
                else
                {
                    instance = this.instanceCreator();
                }

                this.RemoveValidator();
            }
            catch (Exception ex)
            {
                this.validator.Reset();

                throw this.GetErrorForTryingToGetInstanceOfType(ex);
            }

            if (instance == null)
            {
                throw new ActivationException(StringResources.DelegateForTypeReturnedNull(this.ServiceType));
            }

            return instance;
        }

        /// <summary>
        /// Builds an expression that expresses the intent to get an instance by the current producer. A call 
        /// to this method locks the container. No new registrations can't be made after a call to this method.
        /// </summary>
        /// <returns>An Expression.</returns>
        public Expression BuildExpression()
        {
            this.validator.CheckForRecursiveCalls();

            try
            {
                return this.lazyExpression.Value;
            }
            catch (Exception ex)
            {
                throw this.GetErrorForTryingToGetInstanceOfType(ex);
            }
            finally
            {
                // NOTE:  We don't remove the cyclic dependency validator while building the expression.
                // Instead we reset it so it can be checked later on again. We do this because only if 
                // GetInstance has been called we can know for sure that there's no cyclic dependency. There 
                // could be a 'runtime cyclic dependency' caused by a registered delegate that calls back into 
                // the container manually. This will not be detected during building the expression, because 
                // the delegate won't (always) get executed at this point.
                this.validator.Reset();
            }
        }

        /// <summary>
        /// Gets the collection of relationships for this instance that the container knows about.
        /// This includes relationships between the registered type and its dependencies and relationships 
        /// between applied decorators and their dependencies. Note that types that are not newed up by the 
        /// container and properties that are injected inside a custom delegate that is registered using the
        /// <see cref="Container.RegisterInitializer{TService}">RegisterInitializer</see> method are unknown
        /// to the container and are not returned from this method.
        /// Also note that this method will return an empty collection when called before the
        /// registered type is requested from the container (or before <see cref="Container.Verify()">Verify</see>
        /// is called). 
        /// </summary>
        /// <returns>An array of <see cref="KnownRelationship"/> instances.</returns>
        public KnownRelationship[] GetRelationships()
        {
            if (this.knownRelationships != null)
            {
                return this.knownRelationships.ToArray();
            }

            return this.Registration.GetRelationships();
        }

        /// <summary>
        /// Builds a string representation of the object graph with the current instance as root of the
        /// graph.
        /// </summary>
        /// <returns>A string representation of the object graph.</returns>
        /// <exception cref="InvalidOperationException">Thrown when this method is called before 
        /// <see cref="GetInstance"/> or <see cref="BuildExpression"/> have been called. These calls can be
        /// done directly and explicitly by the user on this instance, indirectly by calling
        /// <see cref="GetInstance"/> or <see cref="BuildExpression"/> on an instance that depends on this
        /// instance, or by calling <see cref="Container.Verify()">Verify</see> on the container.</exception>
        public string VisualizeObjectGraph()
        {
            if (!this.IsExpressionCreated)
            {
                throw new InvalidOperationException(
                    StringResources.VisualizeObjectGraphShouldBeCalledAfterTheExpressionIsCreated());
            }

            return this.VisualizeIndentedObjectGraph(indentingDepth: 0);
        }

        internal string VisualizeIndentedObjectGraph(int indentingDepth, HashSet<InstanceProducer> set = null)
        {
            set = set ?? new HashSet<InstanceProducer>(ReferenceEqualityComparer<InstanceProducer>.Instance);

            var visualizedDependencies =
                from relationship in this.GetRelationships()
                let dependency = relationship.Dependency
                let isCyclicGraph = set.Contains(dependency)
                let visualizeSubGraph = isCyclicGraph
                    ? dependency.VisualizeCyclicProducerWithoutDependencies(indentingDepth + 1)
                    : set.AddReturn(dependency).VisualizeIndentedObjectGraph(indentingDepth + 1, set)
                select Environment.NewLine + visualizeSubGraph;

            return string.Format(CultureInfo.InvariantCulture, "{0}{1}({2})",
                new string(' ', indentingDepth * 4),
                this.ImplementationType.ToFriendlyName(),
                string.Join(",", visualizedDependencies));
        }

        internal string VisualizeCyclicProducerWithoutDependencies(int indentingDepth)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}{1}({2})",
                new string(' ', indentingDepth * 4),
                this.ImplementationType.ToFriendlyName(),
                "/* cyclic dependency graph detected */");
        }

        internal string VisualizeInlinedAndTruncatedObjectGraph(int maxLength)
        {
            string implementationName = this.ImplementationType.ToFriendlyName();

            var visualizedDependencies =
                this.VisualizeInlinedDependencies(maxLength - implementationName.Length - 2);

            return string.Format(CultureInfo.InvariantCulture, "{0}({1})",
                implementationName,
                string.Join(", ", visualizedDependencies));
        }

        // Throws an InvalidOperationException on failure.
        internal Expression VerifyExpressionBuilding()
        {
            try
            {
                return this.BuildExpression();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(StringResources.ConfigurationInvalidCreatingInstanceFailed(
                    this.ServiceType, ex), ex);
            }
        }

        // Throws an InvalidOperationException on failure.
        internal object VerifyInstanceCreation()
        {
            object instance;

            try
            {
                // Test the creator
                instance = this.GetInstance();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(StringResources.ConfigurationInvalidCreatingInstanceFailed(
                    this.ServiceType, ex), ex);
            }

            return instance;
        }

        // A verifier is an Action delegate that will be called during the object creation step in the
        // verification process (when the user calls Verify()) to enable verification of the whole object 
        // graph.
        internal void AddVerifier(Action action)
        {
            lock (this.locker)
            {
                if (this.verifiers == null)
                {
                    this.verifiers = new List<Action>();
                }

                this.verifiers.Add(action);
            }
        }

        internal void ReplaceRelationships(IEnumerable<KnownRelationship> relationships)
        {
            this.knownRelationships = new ReadOnlyCollection<KnownRelationship>(relationships.Distinct().ToArray());
        }

        internal void EnsureTypeWillBeExplicitlyVerified()
        {
            this.isValid = null;
        }

        internal void DoExtraVerfication()
        {
            try
            {
                foreach (var verify in this.GetVerifiers())
                {
                    verify();
                }

                this.VerifiersAreSuccessfullyCalled = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(StringResources.ConfigurationInvalidCreatingInstanceFailed(
                    this.ServiceType, ex), ex);
            }
        }

        private Action[] GetVerifiers()
        {
            lock (this.locker)
            {
                return this.verifiers != null ? this.verifiers.ToArray() : NoVerifiers;
            }
        }

        private Func<object> BuildInstanceCreator()
        {
            // Don't do recursive checks. The GetInstance() already does that.
            var expression = this.lazyExpression.Value;

            Func<object> creator;

            try
            {
                creator = this.Registration.Container.WrapWithResolveInterceptor(
                    this.initializationContext,
                        CompilationHelpers.CompileExpression<object>(this.Registration.Container, expression));
            }
            catch (Exception ex)
            {
                string message =
                    StringResources.ErrorWhileBuildingDelegateFromExpression(this.ServiceType, expression, ex);

                throw new ActivationException(message, ex);
            }

            this.Analyze();

            return creator;
        }

        private void Analyze()
        {
            if (!this.Registration.Container.Options.SuppressLifestyleMismatchVerification)
            {
                var error = PotentialLifestyleMismatchAnalyzer.Instance.Analyze(new[] { this })
                    .Cast<PotentialLifestyleMismatchDiagnosticResult>()
                    .FirstOrDefault();

                if (error != null)
                {
                    throw new DiagnosticVerificationException(
                        StringResources.LifestyleMismatchesReported(error), error);
                }
            }
        }

        private Expression BuildExpressionInternal()
        {
            // We must lock the container, because not locking could lead to race conditions.
            this.Registration.Container.LockContainer();

            var expression = this.Registration.BuildExpression(this);

            if (expression == null)
            {
                throw new ActivationException(StringResources.RegistrationReturnedNullFromBuildExpression(
                    this.Registration));
            }

            var e = new ExpressionBuiltEventArgs(this.ServiceType, expression);

            e.Lifestyle = this.Lifestyle;
            e.InstanceProducer = this;
            e.ReplacedRegistration = this.Registration;

            this.Registration.Container.OnExpressionBuilt(e, this);

            if (!object.ReferenceEquals(this.Registration, e.ReplacedRegistration))
            {
                this.Registration = e.ReplacedRegistration;
            }
            else
            {
                this.overriddenLifestyle = e.Lifestyle;
            }

            return e.Expression;
        }

        private ActivationException GetErrorForTryingToGetInstanceOfType(Exception innerException)
        {
            string exceptionMessage;

            if (this.IsContainerAutoRegistered)
            {
                exceptionMessage = StringResources.ImplicitRegistrationCouldNotBeMadeForType(this.ServiceType);
            }
            else
            {
                exceptionMessage = StringResources.DelegateForTypeThrewAnException(this.ServiceType);
            }

            return new ActivationException(exceptionMessage + " " + innerException.Message, innerException);
        }

        // This method will be inlined by the JIT.
#if NET45
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private void RemoveValidator()
        {
            // No recursive calls detected, we can remove the validator to increase performance.
            // We first check for null, because this is faster. Every time we write, the CPU has to send
            // the new value to all the other CPUs. We only nullify the validator while using the GetInstance
            // method, because the BuildExpression will only be called a limited amount of time.
            if (this.validator != null)
            {
                this.validator = null;
            }
        }

        private Exception GetExceptionIfInvalid()
        {
            try
            {
                // Test if the instance can be made.
                this.BuildExpression();

                return null;
            }
            catch (ActivationException ex)
            {
                return ex.InnerException ?? ex;
            }
        }

        private IEnumerable<string> VisualizeInlinedDependencies(int maxLength)
        {
            var relationships = new Stack<KnownRelationship>(this.GetRelationships().Reverse());

            if (!relationships.Any())
            {
                yield break;
            }

            while (maxLength > 0 && relationships.Any())
            {
                var relationship = relationships.Pop();

                bool lastDependency = !relationships.Any();

                string childGraph = relationship.Dependency.VisualizeInlinedAndTruncatedObjectGraph(
                    !lastDependency ? maxLength - ", ...".Length : maxLength);

                maxLength -= childGraph.Length;

                bool displayingThisGraphWillCauseAnOverflow =
                    (!lastDependency && maxLength < ", ...".Length) || maxLength < 0;

                if (displayingThisGraphWillCauseAnOverflow)
                {
                    yield return "...";
                    yield break;
                }
                else
                {
                    yield return childGraph;
                }

                maxLength -= ", ".Length;
            }

            if (relationships.Any())
            {
                yield return "...";
            }
        }

        private static bool ShouldBeRegisteredAsAnExternalProducer(Registration registration)
        {
            // ExpressionRegistration is an internal Registration type. An InstanceProducer with this type
            // of registration doesn't have to be registered, since it will either always be registered
            // in the registrations dictionary anyway, or it is used to build up an InstanceProducer (by
            // the decorator sub system) that is only used for diagnostics. Allowing the latter producers to
            // be added, will clutter the diagnostic API and will cause the Verify() method to verify those
            // producers needlessly.   
            return !(registration is ExpressionRegistration);
        }

        internal sealed class InstanceProducerDebugView
        {
            private readonly InstanceProducer instanceProducer;

            internal InstanceProducerDebugView(InstanceProducer instanceProducer)
            {
                this.instanceProducer = instanceProducer;
            }

            public Lifestyle Lifestyle
            {
                get { return this.instanceProducer.Lifestyle; }
            }

            [DebuggerDisplay("{SimpleInjector.Helpers.ToFriendlyName(ServiceType),nq}")]
            public Type ServiceType
            {
                get { return this.instanceProducer.ServiceType; }
            }

            [DebuggerDisplay("{SimpleInjector.Helpers.ToFriendlyName(ImplementationType),nq}")]
            public Type ImplementationType
            {
                get { return this.instanceProducer.ImplementationType; }
            }

            public KnownRelationship[] Relationships
            {
                get { return this.instanceProducer.GetRelationships(); }
            }

            // By using the TruncatedDependencyGraph as value of this DependencyGraph property, we allow the
            // graph to be shown in compact form in the debugger in-line value field, but still allow the
            // complete formatted object graph to be shown when the user opens the text visualizer.
            [DebuggerDisplay(value: "{TruncatedDependencyGraph,nq}")]
            public string DependencyGraph
            {
                get { return this.instanceProducer.VisualizeIndentedObjectGraph(indentingDepth: 0); }
            }

            [DebuggerHidden]
            private string TruncatedDependencyGraph
            {
                get { return this.instanceProducer.VisualizeInlinedAndTruncatedObjectGraph(160); }
            }
        }
    }
}