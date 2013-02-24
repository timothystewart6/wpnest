﻿using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using WPNest.Web;

namespace WPNest.Services {

	public class NestWebService : INestWebService {

		private ISessionProvider _sessionProvider;
		private IAnalyticsService _analyticsService;
		private IWebRequestProvider _webRequestProvider;
		private INestWebServiceDeserializer _deserializer;

		public NestWebService() {
			_deserializer = ServiceContainer.GetService<INestWebServiceDeserializer>();
			_sessionProvider = ServiceContainer.GetService<ISessionProvider>();
			_analyticsService = ServiceContainer.GetService<IAnalyticsService>();
			_webRequestProvider = ServiceContainer.GetService<IWebRequestProvider>();
		}

		public async Task GetStructureAndDeviceStatusAsync(Structure structure) {
			await GetStructureStatusAsync(structure);
		}

		private async Task GetStructureStatusAsync(Structure structure) {
			string url = string.Format("{0}/v2/subscribe", _sessionProvider.TransportUrl);
			var request = GetPostJsonRequest(url);

			SetAuthorizationHeaderOnRequest(request, _sessionProvider.AccessToken);
			SetNestHeadersOnRequest(request, _sessionProvider.UserId);

			string requestString = string.Format("{{\"keys\":[{{\"key\":\"structure.{0}\"}}]}}", structure.ID);
			await request.SetRequestStringAsync(requestString);

			IWebResponse response = await request.GetResponseAsync();
			string responseString = await response.GetResponseStringAsync();
			_deserializer.ParseStructureFromGetStructureStatusResult(responseString, structure.ID);
		}

		public async Task<WebServiceResult> LoginAsync(string userName, string password) {
			IWebRequest request = GetPostFormRequest("https://home.nest.com/user/login");
			string requestString = string.Format("username={0}&password={1}", UrlEncode(userName), UrlEncode(password));
			await request.SetRequestStringAsync(requestString);
			Exception exception;

			try {
				IWebResponse response = await request.GetResponseAsync();
				string responseString = await response.GetResponseStringAsync();
				CacheSession(responseString);
				return new WebServiceResult();
			}
			catch (Exception ex) {
				exception = ex;
			}

			var error = await _deserializer.ParseWebServiceErrorAsync(exception);
			return new WebServiceResult(error, exception);
		}

		public async Task<GetStatusResult> GetFullStatusAsync() {
			if (_sessionProvider.IsSessionExpired)
				return new GetStatusResult(WebServiceError.SessionTokenExpired, new SessionExpiredException());

			string url = string.Format("{0}/v2/mobile/user.{1}", _sessionProvider.TransportUrl, _sessionProvider.UserId);
			var request = GetGetRequest(url);
			SetAuthorizationHeaderOnRequest(request, _sessionProvider.AccessToken);
			Exception exception = null;

			try {
				IWebResponse response = await request.GetResponseAsync();
				string responseString = await response.GetResponseStringAsync();
				var structures = _deserializer.ParseStructuresFromGetStatusResult(responseString, _sessionProvider.UserId);
				_analyticsService.LogEvent("Structures: {0}, Devices: {1}", structures.Count(), structures.Sum(s => s.Thermostats.Count()));
				return new GetStatusResult(structures);
			}
			catch (Exception ex) {
				exception = ex;
			}

			var error = await _deserializer.ParseWebServiceErrorAsync(exception);
			return new GetStatusResult(error, exception);
		}

		private async Task<WebServiceResult> SendPutRequestAsync(string url, string requestJson) {
			if (_sessionProvider.IsSessionExpired)
				return new GetStatusResult(WebServiceError.SessionTokenExpired, new SessionExpiredException());

			IWebRequest request = GetPostJsonRequest(url);
			SetAuthorizationHeaderOnRequest(request, _sessionProvider.AccessToken);
			SetNestHeadersOnRequest(request, _sessionProvider.UserId);

			await request.SetRequestStringAsync(requestJson);
			Exception exception = null;

			try {
				await request.GetResponseAsync();
				return new WebServiceResult();
			}
			catch (Exception ex) {
				exception = ex;
			}

			var error = await _deserializer.ParseWebServiceErrorAsync(exception);
			return new WebServiceResult(error, exception);
		}

		public async Task<WebServiceResult> SetFanModeAsync(Thermostat thermostat, FanMode fanMode) {
			string url = string.Format(@"{0}/v2/put/device.{1}", _sessionProvider.TransportUrl, thermostat.ID);
			string fanModeString = GetFanModeString(fanMode);
			string requestString = string.Format("{{\"fan_mode\":\"{0}\"}}", fanModeString);
			return await SendPutRequestAsync(url, requestString);
		}

		public async Task<WebServiceResult> UpdateTransportUrlAsync() {
			IWebRequest request = GetPostJsonRequest("https://home.nest.com/user/service_urls");
			SetAuthorizationHeaderOnRequest(request, _sessionProvider.AccessToken);

			Exception exception = null;
			try {
				IWebResponse response = await request.GetResponseAsync();
				string strContent = await response.GetResponseStringAsync();
				var transportUrl = _deserializer.ParseTransportUrlFromResult(strContent);
				_sessionProvider.UpdateTransportUrl(transportUrl);
				return new WebServiceResult();
			}
			catch (Exception ex) {
				exception = ex;
			}

			var error = await _deserializer.ParseWebServiceErrorAsync(exception);
			return new WebServiceResult(error, exception);
		}

		private static string GetFanModeString(FanMode fanMode) {
			if (fanMode == FanMode.Auto)
				return "auto";
			if (fanMode == FanMode.On)
				return "on";

			throw new InvalidOperationException();
		}

		public async Task<WebServiceResult> ChangeTemperatureAsync(Thermostat thermostat, double desiredTemperature) {
			string url = string.Format(@"{0}/v2/put/shared.{1}", _sessionProvider.TransportUrl, thermostat.ID);
			double desiredTempCelcius = desiredTemperature.FahrenheitToCelcius();
			string requestString = string.Format("{{\"target_change_pending\":true,\"target_temperature\":{0}}}", desiredTempCelcius.ToString());
			return await SendPutRequestAsync(url, requestString);
		}

		public async Task<GetThermostatStatusResult> GetThermostatStatusAsync(Thermostat thermostat) {
			if (_sessionProvider.IsSessionExpired)
				return new GetThermostatStatusResult(WebServiceError.SessionTokenExpired, new SessionExpiredException());

			GetThermostatStatusResult result = await GetSharedThermostatPropertiesAsync(thermostat);
			if (result.Exception == null) {
				result = await GetDeviceThermostatPropertiesAsync(thermostat, result);
			}

			return result;
		}

		private async Task<GetThermostatStatusResult> GetDeviceThermostatPropertiesAsync(Thermostat thermostat, GetThermostatStatusResult result) {
			string url = string.Format("{0}/v2/subscribe", _sessionProvider.TransportUrl);
			IWebRequest request = GetPostJsonRequest(url);
			SetAuthorizationHeaderOnRequest(request, _sessionProvider.AccessToken);
			SetNestHeadersOnRequest(request, _sessionProvider.UserId);
			string requestString = string.Format("{{\"keys\":[{{\"key\":\"device.{0}\"}}]}}", thermostat.ID);
			await request.SetRequestStringAsync(requestString);

			Exception exception;
			try {
				IWebResponse response = await request.GetResponseAsync();
				string strContent = await response.GetResponseStringAsync();
				result.Thermostat.FanMode = _deserializer.ParseFanModeFromDeviceSubscribeResult(strContent);
				return result;
			}
			catch (Exception ex) {
				exception = ex;
			}

			var error = await _deserializer.ParseWebServiceErrorAsync(exception);
			return new GetThermostatStatusResult(error, exception);
		}

		private async Task<GetThermostatStatusResult> GetSharedThermostatPropertiesAsync(Thermostat thermostat) {
			string url = string.Format("{0}/v2/subscribe", _sessionProvider.TransportUrl);
			IWebRequest request = GetPostJsonRequest(url);
			SetAuthorizationHeaderOnRequest(request, _sessionProvider.AccessToken);
			SetNestHeadersOnRequest(request, _sessionProvider.UserId);
			string requestString = string.Format("{{\"keys\":[{{\"key\":\"shared.{0}\"}}]}}", thermostat.ID);
			await request.SetRequestStringAsync(requestString);

			Exception exception;
			try {
				IWebResponse response = await request.GetResponseAsync();
				string strContent = await response.GetResponseStringAsync();
				var updatedThermostat = new Thermostat(thermostat.ID);
				_deserializer.UpdateThermostatStatusFromSharedStatusResult(strContent, updatedThermostat);
				return new GetThermostatStatusResult(updatedThermostat);
			}
			catch (Exception ex) {
				exception = ex;
			}

			var error = await _deserializer.ParseWebServiceErrorAsync(exception);
			return new GetThermostatStatusResult(error, exception);
		}

		private IWebRequest GetGetRequest(string url) {
			IWebRequest request = _webRequestProvider.CreateRequest(new Uri(url));
			request.Method = "GET";
			return request;
		}

		private IWebRequest GetPostFormRequest(string url) {
			IWebRequest request = _webRequestProvider.CreateRequest(new Uri(url));
			request.ContentType = ContentType.Form;
			request.Method = "POST";
			return request;
		}

		private IWebRequest GetPostJsonRequest(string url) {
			IWebRequest request = _webRequestProvider.CreateRequest(new Uri(url));
			request.ContentType = ContentType.Json;
			request.Method = "POST";
			return request;
		}

		private void CacheSession(string responseString) {
			var accessToken = _deserializer.ParseAccessTokenFromLoginResult(responseString);
			var accessTokenExpirationDate = _deserializer.ParseAccessTokenExpiryFromLoginResult(responseString);
			var userId = _deserializer.ParseUserIdFromLoginResult(responseString);
			var transportUrl = _deserializer.ParseTransportUrlFromResult(responseString);

			_sessionProvider.SetSession(transportUrl, userId, accessToken, accessTokenExpirationDate);
		}

		private string UrlEncode(string value) {
			return HttpUtility.UrlEncode(value);
		}

		private static void SetAuthorizationHeaderOnRequest(IWebRequest request, string accessToken) {
			request.Headers["Authorization"] = string.Format("Basic {0}", accessToken);
		}

		private static void SetNestHeadersOnRequest(IWebRequest request, string userId) {
			request.Headers["X-nl-protocol-version"] = "1";
			request.Headers["X-nl-user-id"] = userId;
		}
	}
}
