using System;
using System.Collections.Generic;
using System.Linq;
using NuKeeper.Abstractions;
using NuKeeper.Abstractions.CollaborationPlatform;
using NuKeeper.Abstractions.Configuration;
using NuKeeper.Abstractions.Formats;
using NuKeeper.Abstractions.Logging;
using NuKeeper.AzureDevOps;
using NuKeeper.BitBucket;
using NuKeeper.GitHub;

namespace NuKeeper.Collaboration
{
    public class CollaborationFactory : ICollaborationFactory
    {
        private readonly IEnumerable<ISettingsReader> _settingReaders;
        private readonly INuKeeperLogger _nuKeeperLogger;
        private Platform? _platform;

        public IForkFinder ForkFinder { get; private set; }

        public IRepositoryDiscovery RepositoryDiscovery { get; private set; }

        public ICollaborationPlatform CollaborationPlatform { get; private set; }

        public CollaborationPlatformSettings Settings { get; }

        public CollaborationFactory(IEnumerable<ISettingsReader> settingReaders,
            INuKeeperLogger nuKeeperLogger)
        {
            _settingReaders = settingReaders;
            _nuKeeperLogger = nuKeeperLogger;
            Settings = new CollaborationPlatformSettings();
        }

        public ValidationResult Initialise(Uri apiEndpoint, string token, ForkMode? forkModeFromSettings)
        {
            var platformSettingsReader = SettingsReaderForPlatform(apiEndpoint);

            _platform = platformSettingsReader.Platform;

            _nuKeeperLogger.Normal($"Matched uri '{apiEndpoint}' to collaboration platform '{_platform}'");

            Settings.BaseApiUrl = UriFormats.EnsureTrailingSlash(apiEndpoint);
            Settings.Token = token;
            Settings.ForkMode = forkModeFromSettings;
            platformSettingsReader.UpdateCollaborationPlatformSettings(Settings);

            var result = ValidateSettings();
            if (!result.IsSuccess)
            {
                return result;
            }

            CreateForPlatform();

            return ValidationResult.Success;
        }

        private ISettingsReader SettingsReaderForPlatform(Uri apiEndpoint)
        {
            var platformSettingsReader = _settingReaders
                .FirstOrDefault(s => s.CanRead(apiEndpoint));

            if (platformSettingsReader == null)
            {
                throw new NuKeeperException($"Unable to find collaboration platform for uri {apiEndpoint}");
            }

            return platformSettingsReader;
        }

        private ValidationResult ValidateSettings()
        {
            if (!Settings.BaseApiUrl.IsWellFormedOriginalString()
                || (Settings.BaseApiUrl.Scheme != "http" && Settings.BaseApiUrl.Scheme != "https"))
            {
                return ValidationResult.Failure(
                    $"Api is not of correct format {Settings.BaseApiUrl}");
            }

            if (!Settings.ForkMode.HasValue)
            {
                return ValidationResult.Failure("Fork Mode was not set");
            }

            if (string.IsNullOrWhiteSpace(Settings.Token))
            {
                return ValidationResult.Failure("Token was not set");
            }

            if (!_platform.HasValue)
            {
                return ValidationResult.Failure("Platform was not set");
            }

            return ValidationResult.Success;
        }

        private void CreateForPlatform()
        {
            var forkMode = Settings.ForkMode.Value;

            switch (_platform.Value)
            {
                case Platform.AzureDevOps:
                    CollaborationPlatform = new AzureDevOpsPlatform(_nuKeeperLogger);
                    RepositoryDiscovery = new AzureDevOpsRepositoryDiscovery(_nuKeeperLogger, Settings.Token);
                    ForkFinder = new AzureDevOpsForkFinder(CollaborationPlatform, _nuKeeperLogger, forkMode);
                    break;

                case Platform.GitHub:
                    CollaborationPlatform = new OctokitClient(_nuKeeperLogger);
                    RepositoryDiscovery = new GitHubRepositoryDiscovery(_nuKeeperLogger, CollaborationPlatform);
                    ForkFinder = new GitHubForkFinder(CollaborationPlatform, _nuKeeperLogger, forkMode);
                    break;

                case Platform.Bitbucket:
                    CollaborationPlatform = new BitbucketPlatform(_nuKeeperLogger);
                    RepositoryDiscovery = new BitbucketRepositoryDiscovery(_nuKeeperLogger);
                    ForkFinder = new BitbucketForkFinder(CollaborationPlatform, _nuKeeperLogger, forkMode);
                    break;

                default:
                    throw new NuKeeperException($"Unknown platform: {_platform}");
            }

            var auth = new AuthSettings(Settings.BaseApiUrl, Settings.Token, Settings.Username);
            CollaborationPlatform.Initialise(auth);

            if (ForkFinder == null ||
                RepositoryDiscovery == null ||
                CollaborationPlatform == null)
            {
                throw new NuKeeperException($"Platform {_platform} could not be initialised");
            }
        }
    }
}