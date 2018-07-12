﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gigya.Microdot.Interfaces.Logging;
using Gigya.Microdot.Interfaces.SystemWrappers;
using Gigya.Microdot.ServiceDiscovery.Config;
using Gigya.Microdot.SharedLogic.Monitor;
using Metrics;

namespace Gigya.Microdot.ServiceDiscovery.Rewrite
{

    /// <summary>
    /// Monitors Consul using KeyValue api, to get a list of all available <see cref="Services"/>.
    /// Creates instances of <see cref="ConsulNodeSource"/> using the list of known services.
    /// </summary>
    internal sealed class ConsulNodeSourceFactory : INodeSourceFactory
    {
        ILog Log { get; }
        private ConsulClient ConsulClient { get; }
        private Func<DeploymentIdentifier, ConsulNodeSource> CreateConsulNodeSource { get; }
        private IDateTime DateTime { get; }
        private Func<ConsulConfig> GetConfig { get; }

        private HealthCheckResult _healthStatus = HealthCheckResult.Healthy("Initializing...");
        private readonly ComponentHealthMonitor _serviceListHealthMonitor;

        private readonly CancellationTokenSource _shutdownToken = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _initCompleted = new TaskCompletionSource<bool>();



        /// <inheritdoc />
        public ConsulNodeSourceFactory(ILog log, ConsulClient consulClient, Func<DeploymentIdentifier,
            ConsulNodeSource> createConsulNodeSource, IDateTime dateTime, Func<ConsulConfig> getConfig, IHealthMonitor healthMonitor)
        {
            Log = log;
            ConsulClient = consulClient;
            CreateConsulNodeSource = createConsulNodeSource;
            DateTime = dateTime;
            GetConfig = getConfig;            

            _serviceListHealthMonitor = healthMonitor.SetHealthFunction("ConsulServiceList", () => _healthStatus); // nest under "Consul" along with other consul-related healths.
            Task.Run(() => GetAllLoop());
        }



        public string Type => "Consul";



        public async Task<INodeSource> CreateNodeSource(DeploymentIdentifier deploymentIdentifier)
        {
            await _initCompleted.Task.ConfigureAwait(false);

            if (await IsServiceDeployed(deploymentIdentifier).ConfigureAwait(false))
            {
                var consulNodeSource = CreateConsulNodeSource(deploymentIdentifier);
                await consulNodeSource.Init().ConfigureAwait(false);
                return consulNodeSource;
            }
            return null;
        }

        private Exception Error { get; set; }


        public async Task<bool> IsServiceDeployed(DeploymentIdentifier deploymentIdentifier)
        {
            await _initCompleted.Task.ConfigureAwait(false);
            if (Services.Count == 0 && Error != null)
                throw Error;

            return Services.Contains(deploymentIdentifier.ToString());
        }

        // We can't store DeploymentIdentifier's here since we can't reliably parse them when they return from Consul
        // as strings (due to '-' separators in service names)
        HashSet<string> Services = new HashSet<string>();


        private async void GetAllLoop()
        {
            try
            {
                ulong? modifyIndex = null;
                while (!_shutdownToken.IsCancellationRequested)
                {
                    modifyIndex = await GetAllServices(modifyIndex ?? 0).ConfigureAwait(false);
                    _initCompleted.TrySetResult(true);

                    // If we got an error, we don't want to spam Consul so we wait a bit
                    if (modifyIndex == null)
                        await DateTime.Delay(GetConfig().ErrorRetryInterval, _shutdownToken.Token).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException) when (_shutdownToken.IsCancellationRequested)
            {
                // Ignore exception during shutdown.
            }
        }


        private async Task<ulong?> GetAllServices(ulong modifyIndex)
        {
            var consulResult = await ConsulClient.GetAllServices(modifyIndex, _shutdownToken.Token).ConfigureAwait(false);

            if (consulResult.Error != null)
            {
                if (consulResult.Error.InnerException is TaskCanceledException == false)
                {
                    Log.Error("Error calling Consul to get all services list", exception: consulResult.Error, unencryptedTags: new
                    {
                        consulAddress = consulResult.ConsulAddress,
                        commandPath   = consulResult.CommandPath,
                        responseCode  = consulResult.StatusCode,
                        content       = consulResult.ResponseContent
                    });
                }

                _healthStatus = HealthCheckResult.Unhealthy($"Error calling Consul: {consulResult.Error.Message}");
                Error = consulResult.Error;
                return null;
            }
            else
            {
                Services = new HashSet<string>(consulResult.Result);
                _healthStatus = HealthCheckResult.Healthy(string.Join("\r\n", Services));
                Error = null;
                return consulResult.ModifyIndex;
            }
        }


        private int _disposed;

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposed) != 1)
                return;

            _shutdownToken.Cancel();
            _shutdownToken.Dispose();
            _serviceListHealthMonitor.Dispose();
        }
        
    }

}