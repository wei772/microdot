﻿#region Copyright 
// Copyright 2017 Gigya Inc.  All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License"); 
// you may not use this file except in compliance with the License.  
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDER AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
#endregion

using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks.Dataflow;
using Gigya.Microdot.Configuration;
using Gigya.Microdot.Configuration.Objects;
using Gigya.Microdot.Hosting.Validators;
using Gigya.Microdot.Interfaces;
using Ninject;

namespace Gigya.Microdot.Ninject.SystemInitializer
{
    public class SystemInitializer : IDisposable
    {
        private IKernel _kernel;
        private IConfigObjectsCache _configObjectsCache;
        private ISourceBlock<ServicePointManagerDefaultConfig> _configSource;

        public SystemInitializer(IKernel kernel, IConfigObjectsCache configObjectsCache)
        {
            _kernel = kernel;
            _configObjectsCache = configObjectsCache;
        }

        public void Init()
        {
            RunValidations();
            SearchAssembliesAndRebindIConfig();
            SetDefaultTCPHTTPSettings();
        }

        private void RunValidations()
        {
            _kernel.Get<ServiceValidator>().Validate();
        }

        private void SearchAssembliesAndRebindIConfig()
        {
            IAssemblyProvider aProvider = _kernel.Get<IAssemblyProvider>();
            foreach (Type configType in aProvider.GetAllTypes().Where(ConfigObjectCreator.IsConfigObject))
            {
                IConfigObjectCreator configObjectCreator = _kernel.Get<Func<Type, IConfigObjectCreator>>()(configType);

                dynamic getLatestLambda = configObjectCreator.GetLambdaOfGetLatest(configType);
                _kernel.Rebind(typeof(Func<>).MakeGenericType(configType)).ToMethod(t => getLatestLambda());

                Type sourceBlockType = typeof(ISourceBlock<>).MakeGenericType(configType);
                _kernel.Rebind(sourceBlockType).ToMethod(t => configObjectCreator.ChangeNotifications);

                dynamic changeNotificationsLambda = configObjectCreator.GetLambdaOfChangeNotifications(sourceBlockType);
                _kernel.Rebind(typeof(Func<>).MakeGenericType(sourceBlockType)).ToMethod(t => changeNotificationsLambda());

                _kernel.Rebind(configType).ToMethod(t => configObjectCreator.GetLatest());

                _configObjectsCache.RegisterConfigObjectCreator(configObjectCreator);
            }
        }

        protected virtual void SetDefaultTCPHTTPSettings()
        {
            _configSource = _kernel.Get<ISourceBlock<ServicePointManagerDefaultConfig>>();
            _configSource.LinkTo(new ActionBlock<ServicePointManagerDefaultConfig>(cnf => SetServicePointManagerDefaultValues(cnf)));

            ServicePointManagerDefaultConfig config = _kernel.Get<Func<ServicePointManagerDefaultConfig>>()();
            SetServicePointManagerDefaultValues(config);
        }

        private void SetServicePointManagerDefaultValues(ServicePointManagerDefaultConfig config)
        {
            ServicePointManager.DefaultConnectionLimit = config.DefaultConnectionLimit;
            ServicePointManager.UseNagleAlgorithm = config.UseNagleAlgorithm;
            ServicePointManager.Expect100Continue = config.Expect100Continue;
        }

        public virtual void Dispose()
        {
            _configSource.Complete();
        }
    }
}
