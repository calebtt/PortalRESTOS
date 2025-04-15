using ApiHelpers;
using GeoHash.NetCore.Enums;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using System.Collections.Concurrent;
using System.Net.Mail;
using System.Net;
using System.Text.Json;
using EventbriteDotNet.Extensions;

namespace PortalREST;

public class Routing
{
    // Request model for canceling an order.
    public class CancelOrderRequest
    {
        public ulong OrderId { get; set; }
        public string Reason { get; set; } = string.Empty; // Requires a reason for cancellation
    }
    // Email request model
    public class EmailRequest
    {
        public string Notes { get; set; } = "";
    }

    private const GeoHashPrecision _hashPrecision = GeoHashPrecision.Level8;

    private static readonly ConcurrentDictionary<string, List<Place>> savedPlaces = new();
    private static string _emailServer;
    private static string _emailAddress;
    private static string _emailPass;
    private static string _googleApiKey;

    public static void MapGoogleRoutes(WebApplication app, string googleApiKey, string ticketMasterApiKey)
    {
        app.MapPost("/searchGooglePlaces", async (HttpContext http) =>
        {
            // Parse the request body
            var requestBody = await http.Request.ReadFromJsonAsync<SearchGooglePlacesRequest>();
            if(requestBody is null)
            {
                return Results.BadRequest(new { error = "Missing request." });
            }

            bool isMissingBothPlaceTypesAndKeyword = !requestBody.PlaceTypes.Any()
                && (string.IsNullOrEmpty(requestBody.Keyword) || string.IsNullOrWhiteSpace(requestBody.Keyword));

            if (isMissingBothPlaceTypesAndKeyword)
            {
                return Results.BadRequest(new { error = "Missing required parameters, place types or keyword." });
            }

            double? latitude = requestBody.Latitude;
            double? longitude = requestBody.Longitude;
            int searchRadius = requestBody.SearchRadius > 0 ? requestBody.SearchRadius : 5000; // Default to 5000m (5km)
            string? keyword = requestBody.Keyword;

            // Ensure at least one method of location is provided
            if (!latitude.HasValue || !longitude.HasValue)
            {
                if (string.IsNullOrEmpty(requestBody.LocationBias))
                {
                    return Results.BadRequest(new { error = "Either a location bias or an address (latitude/longitude) is required." });
                }

                // Use location bias if available
                (latitude, longitude) = GooglePlacesApi.AdjustLocationForBias(
                    requestBody.LocationBias, GooglePlacesApi.NashvilleLat, GooglePlacesApi.NashvilleLong);
            }

            try
            {
                List<Place> googlePlacesData;
                bool useKeywordSearch = keyword.HasValue()
                    && !string.IsNullOrEmpty(keyword)
                    && !string.IsNullOrWhiteSpace(keyword);
                bool searchByDistance = requestBody.SearchByDistanceVsPopularity == "DISTANCE";

                if (useKeywordSearch)
                {
                    // Fetch places from Google API using the specified radius
                    googlePlacesData = await GooglePlacesApi.GetAllTextSearchResults(
                        googleApiKey,
                        keyword,
                        latitude.Value,
                        longitude.Value,
                        searchRadius, // Use selected search radius
                        searchByDistance
                    );
                }
                else
                {
                    // Fetch places from Google API using the specified radius
                    googlePlacesData = await GooglePlacesApi.GetAllNearbySearchResults(
                        googleApiKey,
                        latitude.Value,
                        longitude.Value,
                        searchRadius, // Use selected search radius
                        requestBody.PlaceTypes,
                        searchByDistance
                    );

                    // Sort by place type matches, if available.
                    googlePlacesData = GooglePlacesApi.GetPlacesSortedByTypeMatches(googlePlacesData, requestBody.PlaceTypes);
                }

                // Filter only operational businesses
                var filteredPlaces = googlePlacesData.Where(x =>
                    string.IsNullOrEmpty(x.BusinessStatus) || x.BusinessStatus == "OPERATIONAL");

                // Filter by availability
                filteredPlaces = GooglePlacesApi.FilterPlacesByAvailability(
                    googlePlacesData,
                    requestBody.DateTimeRanges);

                // Apply Boolean filters dynamically
                filteredPlaces = ApplyBooleanFilters(filteredPlaces, requestBody);

                return Results.Json(filteredPlaces ?? new List<Place>());
            }
            catch (Exception ex)
            {
                Log.Error($"Error fetching Google Places data: {ex.Message}");
                return Results.StatusCode(500);
            }
        });

        // Helper function to apply boolean filters dynamically
        static IEnumerable<Place> ApplyBooleanFilters(IEnumerable<Place> places, SearchGooglePlacesRequest request)
        {
            return places.Where(place =>
                (request.GoodForChildren == null || place.GoodForChildren == request.GoodForChildren) &&
                (request.CurbsidePickup == null || place.CurbsidePickup == request.CurbsidePickup) &&
                (request.Reservable == null || place.Reservable == request.Reservable) &&
                (request.ServesBeer == null || place.ServesBeer == request.ServesBeer) &&
                (request.ServesWine == null || place.ServesWine == request.ServesWine) &&
                (request.ServesVegetarianFood == null || place.ServesVegetarianFood == request.ServesVegetarianFood) &&
                (request.OutdoorSeating == null || place.OutdoorSeating == request.OutdoorSeating) &&
                (request.LiveMusic == null || place.LiveMusic == request.LiveMusic) &&
                (request.GoodForWatchingSports == null || place.GoodForWatchingSports == request.GoodForWatchingSports) &&
                (request.GoodForGroups == null || place.GoodForGroups == request.GoodForGroups) &&
                (request.ServesCocktails == null || place.ServesCocktails == request.ServesCocktails) &&
                (request.ServesCoffee == null || place.ServesCoffee == request.ServesCoffee) &&
                (request.AllowsDogs == null || place.AllowsDogs == request.AllowsDogs) &&
                (request.Restroom == null || place.Restroom == request.Restroom) &&
                // ✅ New Accessibility Filters
                (request.WheelchairAccessibleParking == null ||
                 (place.AccessibilityOptions != null && place.AccessibilityOptions.WheelchairAccessibleParking == request.WheelchairAccessibleParking)) &&

                (request.WheelchairAccessibleEntrance == null ||
                 (place.AccessibilityOptions != null && place.AccessibilityOptions.WheelchairAccessibleEntrance == request.WheelchairAccessibleEntrance)) &&

                (request.WheelchairAccessibleRestroom == null ||
                 (place.AccessibilityOptions != null && place.AccessibilityOptions.WheelchairAccessibleRestroom == request.WheelchairAccessibleRestroom)) &&

                (request.WheelchairAccessibleSeating == null ||
                 (place.AccessibilityOptions != null && place.AccessibilityOptions.WheelchairAccessibleSeating == request.WheelchairAccessibleSeating))
            );
        }

        app.MapPost("/getEvents", async (HttpContext http) =>
        {
            try
            {
                using var reader = new StreamReader(http.Request.Body);
                var requestBodyRaw = await reader.ReadToEndAsync();
                //Console.WriteLine($"Raw JSON Received: {requestBodyRaw}");

                // Manually deserialize with error handling
                var requestBody = JsonSerializer.Deserialize<GetEventsRequest>(requestBodyRaw, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                // Parse request body
                if (requestBody?.Place == null || requestBody.Place.Location == null)
                {
                    return Results.BadRequest(new { error = "Invalid Google Place result." });
                }

                var googlePlace = requestBody.Place;
                var latitude = googlePlace.Location.Latitude;
                var longitude = googlePlace.Location.Longitude;
                var placeName = googlePlace.DisplayName?.Text ?? "";
                var validatedPlaceAddress = googlePlace.FormattedAddress ?? "";

                //Console.WriteLine($"/getEvents route for lat:{latitude}, lon:{longitude}, validated addr:{validatedPlaceAddress}, name:{placeName}");

                // Generate GeoHash from the provided latitude/longitude
                var geoHash = VenueEventMatcher.GetGeoHash(latitude, longitude, _hashPrecision);

                // 2-step process: get venues, then events for venue by venue ID
                var venueList = await VenueEventMatcher.GetUnvalidatedVenuesByGeoHashAndKeyword(googleApiKey, ticketMasterApiKey, geoHash, placeName);

                // Try to match venues using lat/lon proximity and/or address validation
                var bestMatchTuple = PlaceToVenueMatcher.FindBestMatch(googlePlace, venueList);
                var bestMatch = bestMatchTuple.Item1;

                if (bestMatch is null)
                {
                    Log.Information($"Error locating matching TicketMaster venue, possibly not in Ticketmaster DB.");
                    return Results.Json(new TicketMasterVenueInfo());
                    //return Results.StatusCode(500);
                }

                // Fetch events for the matched venue
                var venueGeoHash = bestMatch?.GetGeoHash(_hashPrecision);
                var eventResults = await VenueEventMatcher.GetEventsForLocationByGeoHash(
                    ticketMasterApiKey,
                    bestMatch.Id,
                    venueGeoHash ?? "");

                var matchingEvents = eventResults.Where(x => x.VenueId == bestMatch.Id || x.VenueName == bestMatch.Name);

                return Results.Json(matchingEvents);
            }
            catch (Exception ex)
            {
                Log.Warning($"Error fetching Ticketmaster events: {ex.Message}");
                return Results.StatusCode(500);
            }
        });

        app.MapPost("/checkVenueForPlace", async (HttpContext http) =>
        {
            const double venueMatchConfidenceCutoff = 55.0;
            try
            {
                // Deserialize request body
                var googlePlace = await http.Request.ReadFromJsonAsync<Place>();

                if (googlePlace == null || googlePlace.Location == null || googlePlace.DisplayName == null)
                {
                    return Results.BadRequest(new { error = "Invalid Google Place data." });
                }

                //Console.WriteLine($"Received /checkVenueForPlace request for: {googlePlace.DisplayName.Text}");

                if (!VenueEventMatcher.IsValidNumber(googlePlace.Location.Latitude)
                    || !VenueEventMatcher.IsValidNumber(googlePlace.Location.Longitude))
                {
                    Log.Error("Error: Invalid latitude or longitude.");
                    return Results.BadRequest(new { error = "Invalid latitude or longitude values." });
                }

                var latitude = googlePlace.Location.Latitude;
                var longitude = googlePlace.Location.Longitude;
                var placeName = googlePlace.DisplayName.Text;

                // Generate GeoHash from the provided latitude/longitude
                var geoHash = VenueEventMatcher.GetGeoHash(latitude, longitude, _hashPrecision);

                // Step 1: Get venues using the GeoHash and keyword search
                var venueList = await VenueEventMatcher.GetUnvalidatedVenuesByGeoHashAndKeyword(
                    googleApiKey, ticketMasterApiKey, geoHash, placeName);

                // Step 2: Try to match the given place to a known venue
                var (bestMatch, confidenceScore) = PlaceToVenueMatcher.FindBestMatch(googlePlace, venueList);

                bool hasVenue = bestMatch is not null && confidenceScore > venueMatchConfidenceCutoff;

                // Return structured JSON response
                return Results.Json(new { hasVenue });
            }
            catch (JsonException jsonEx)
            {
                Log.Error($"JSON Parsing Error: {jsonEx}");
                return Results.BadRequest(new { error = "Invalid JSON format." });
            }
            catch (Exception ex)
            {
                Log.Error($"Error checking venue: {ex.Message}");
                return Results.StatusCode(500);
            }
        });

        app.MapPost("/geocodeAddress", async (HttpContext http) =>
        {
            try
            {
                // Parse the request body
                var requestBody = await http.Request.ReadFromJsonAsync<GeocodeRequest>();

                if (requestBody == null || string.IsNullOrEmpty(requestBody.Address))
                {
                    return Results.BadRequest(new { error = "Missing address parameter." });
                }

                string googleGeocodeApiUrl = $"https://maps.googleapis.com/maps/api/geocode/json?address={Uri.EscapeDataString(requestBody.Address)}&key={googleApiKey}";

                using var httpClient = new HttpClient();
                var response = await httpClient.GetStringAsync(googleGeocodeApiUrl);

                var geocodeResponse = JsonSerializer.Deserialize<GoogleGeocodeResponse>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (geocodeResponse?.Status == "OK" && geocodeResponse.Results.Any())
                {
                    var location = geocodeResponse.Results.First().Geometry.Location;
                    return Results.Json(new { latitude = location.Lat, longitude = location.Lng });
                }

                return Results.BadRequest(new { error = "Failed to geocode address." });
            }
            catch (Exception ex)
            {
                Log.Error($"Error in geocoding address: {ex.Message}");
                return Results.StatusCode(500);
            }
        });

        app.MapGet("/getGoogleTypesByCategory", async (HttpContext http) =>
        {
            var placeTypesByCategory = new Dictionary<string, List<string>>
            {
                { "atmosphereVibes", EventPlacePrediction.atmosphereVibes },
                { "dining", EventPlacePrediction.diningPreferences },
                { "indoorEntertainment", EventPlacePrediction.indoorEntertainment },
                { "arts", EventPlacePrediction.arts },
                { "shopping", EventPlacePrediction.shoppingPreferences },
                { "sports", EventPlacePrediction.sportsRecreation },
                { "accommodations", EventPlacePrediction.accommodations }
            };

            return Results.Json(placeTypesByCategory);
        });

    }

    public static void MapOrderRoutes(WebApplication app)
    {
        // Route to fetch scheduled orders
        app.MapGet("/getScheduledOrders", async (HttpContext http) =>
        {
            if (!http.Request.Headers.TryGetValue("Authorization", out var password) || !LoginAuth.Authenticate(http))
                return Results.Unauthorized();

            password = StripBearerPrefix(password);

            return Results.Ok(await CustomerOrderAccess.GetScheduledOrders(password));
        });

        // Route to fetch available orders
        app.MapGet("/getAvailableOrders", async (HttpContext http) =>
        {
            if (!http.Request.Headers.TryGetValue("Authorization", out var password) || !LoginAuth.Authenticate(http))
                return Results.Unauthorized();

            password = StripBearerPrefix(password);

            return Results.Ok(await CustomerOrderAccess.GetAvailableOrders(password));
        });

        // Route to fetch assigned orders for the authenticated employee
        app.MapGet("/getAssignedOrders", async (HttpContext http) =>
        {
            if (!http.Request.Headers.TryGetValue("Authorization", out var password) || !LoginAuth.Authenticate(http))
                return Results.Unauthorized();

            password = StripBearerPrefix(password);
            return Results.Ok(await CustomerOrderAccess.GetAssignedOrders(password));
        });

        // Route to fetch completed orders
        app.MapGet("/getCompletedOrders", async (HttpContext http) =>
        {
            if (!http.Request.Headers.TryGetValue("Authorization", out var password) || !LoginAuth.Authenticate(http))
                return Results.Unauthorized();

            password = StripBearerPrefix(password);
            return Results.Ok(await CustomerOrderAccess.GetCompletedOrders(password));
        });

        app.MapPost("/assignOrder", async (HttpContext http, [FromBody] AssignOrderRequest request) =>
        {
            if (!http.Request.Headers.TryGetValue("Authorization", out var password) || !LoginAuth.Authenticate(http))
                return Results.Unauthorized();

            password = StripBearerPrefix(password);

            bool success = await CustomerOrderAccess.TryAssignOrder(request.OrderId, password);

            if (!success)
            {
                return Results.Conflict(new { error = "Order already assigned to another user or unavailable." });
            }

            return Results.Ok(new { message = "Order assigned successfully." });
        });

        // Complete an order
        app.MapPost("/completeOrder", async (HttpContext http, [FromBody] CompleteOrderRequest request) =>
        {
            if (!http.Request.Headers.TryGetValue("Authorization", out var password) || !LoginAuth.Authenticate(http))
                return Results.Unauthorized();

            password = StripBearerPrefix(password);
            if (!await CustomerOrderAccess.CompleteOrder(request.OrderId, password))
                return Results.NotFound(new { error = "Order not found in assigned list." });

            return Results.Ok(new { message = "Order marked as completed.", orderId = request.OrderId });
        });

        // Cancel an assigned order and return it to availableOrders
        app.MapPost("/cancelOrder", async (HttpContext http, [FromBody] CancelOrderRequest request) =>
        {
            if (!http.Request.Headers.TryGetValue("Authorization", out var password) || !LoginAuth.Authenticate(http))
                return Results.Unauthorized();

            password = StripBearerPrefix(password);
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { error = "Cancellation reason is required." });
            }

            if (!await CustomerOrderAccess.CancelOrder(request.OrderId, password, request.Reason))
                return Results.NotFound(new { error = "Order not found in assigned list or unable to cancel." });

            Log.Information(
                $"[CANCEL ORDER] Order {request.OrderId} canceled by {password}. Reason: {request.Reason}");

            return Results.Ok(new
            {
                message = "Order canceled and returned to available orders.",
                orderId = request.OrderId,
                reason = request.Reason
            });
        });

        // Return order to queue for a valid reason.
        app.MapPost("/returnOrderToQueue", async (HttpContext http) =>
        {
            try
            {
                if (!LoginAuth.Authenticate(http, out string strippedPass))
                {
                    return Results.Json(new { error = "Unauthorized. Invalid employee password." }, statusCode: 401);
                }

                var requestBody = await http.Request.ReadFromJsonAsync<Dictionary<string, object>>();

                if (requestBody == null ||
                    !requestBody.TryGetValue("orderId", out object orderIdObj) ||
                    !ulong.TryParse(orderIdObj.ToString(), out ulong orderId) ||
                    !requestBody.TryGetValue("returnReason", out object returnReasonObj) ||
                    string.IsNullOrWhiteSpace(returnReasonObj.ToString()))
                {
                    return Results.Json(new { error = "Invalid request. Order ID or return reason missing." }, statusCode: 400);
                }

                string returnReason = returnReasonObj.ToString();

                // Unassign order and update metadata
                bool success = await CustomerOrderAccess.UnassignOrder(orderId, strippedPass, returnReason);

                if (!success)
                {
                    return Results.Json(new { error = "Failed to return order to queue." }, statusCode: 500);
                }

                Log.Information($"Order {orderId} returned to queue with reason: {returnReason}");

                return Results.Json(new { message = $"Order {orderId} returned to queue.", returnReason });
            }
            catch (Exception ex)
            {
                Log.Error($"Error returning order to queue: {ex.Message}");
                return Results.Json(new { error = "An internal server error occurred.", details = ex.Message }, statusCode: 500);
            }
        });

        // Route to move canceled orders back to processing
        app.MapPost("/moveCanceledOrdersToProcessing", async (HttpContext http) =>
        {
            bool success = await WCHelper.MoveCanceledOrdersToProcessing();

            if (!success)
                return Results.BadRequest(new { error = "No canceled orders found or update failed." });

            return Results.Ok(new { message = "Canceled orders successfully moved back to processing." });
        });
    }

    public static void MapSavePlaceRoutes(WebApplication app, string emailServer, string emailAddress, string emailPass, string googleApiKey)
    {
        _emailAddress = emailAddress;
        _emailPass = emailPass;
        _emailServer = emailServer;
        _googleApiKey = googleApiKey;

        // Save a place
        app.MapPost("/savePlaceById", async (HttpContext http) =>
        {
            try
            {
                if (!LoginAuth.Authenticate(http, out string strippedPass))
                {
                    return Results.Json(new { error = "Unauthorized. Invalid employee password." }, statusCode: 401);
                }

                var requestBody = await http.Request.ReadFromJsonAsync<Dictionary<string, string>>();

                if (requestBody == null || !requestBody.TryGetValue("placeId", out string placeId) || string.IsNullOrWhiteSpace(placeId))
                {
                    return Results.Json(new { error = "Missing or invalid placeId." }, statusCode: 400);
                }

                // Fetch place details using the given placeId (replace this with actual database/API call)
                var place = await GooglePlacesApi.GetPlaceDetailsById(_googleApiKey, placeId); // Assume this function retrieves the place

                if (place == null)
                {
                    return Results.Json(new { error = "Place not found." }, statusCode: 404);
                }

                lock (savedPlaces)
                {
                    if (!savedPlaces.ContainsKey(strippedPass))
                    {
                        savedPlaces[strippedPass] = new List<Place>();
                    }
                    savedPlaces[strippedPass].Add(place);
                }

                return Results.Json(new { message = "Place saved successfully." });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "An internal server error occurred.", details = ex.Message }, statusCode: 500);
            }
        });


        // Get saved places for authenticated employee
        app.MapGet("/getSavedPlaces", (HttpContext http) =>
        {
            if (!http.Request.Headers.TryGetValue("Authorization", out var employeePassword) || !LoginAuth.Authenticate(http, out string strippedPass))
            {
                return Results.Unauthorized();
            }

            lock (savedPlaces)
            {
                if (savedPlaces.TryGetValue(strippedPass, out var places))
                {
                    return Results.Json(places);
                }
            }

            return Results.Json(new List<Place>());
        });

        // Remove a saved place by ID
        app.MapDelete("/removePlace/{placeId}", (HttpContext http, string placeId) =>
        {
            if (!http.Request.Headers.TryGetValue("Authorization", out var employeePassword) || !LoginAuth.Authenticate(http, out string strippedPass))
            {
                return Results.Unauthorized();
            }

            lock (savedPlaces)
            {
                if (savedPlaces.TryGetValue(strippedPass, out var placeList))
                {
                    var placeToRemove = placeList.FirstOrDefault(p => p.PlaceId == placeId);
                    if (placeToRemove != null)
                    {
                        placeList.Remove(placeToRemove);
                        return Results.Ok(new { message = "Place removed successfully." });
                    }
                }
            }

            return Results.NotFound(new { error = "Place not found." });
        });

        // Define the POST route for sending emails
        app.MapPost("/emailSavedPlaces", async (HttpContext http) =>
        {
            try
            {
                if (!http.Request.Headers.TryGetValue("Authorization", out var employeePassword) ||
                    !LoginAuth.Authenticate(http, out string strippedPass))
                {
                    return Results.Unauthorized();
                }

                var requestBody = await http.Request.ReadFromJsonAsync<EmailRequest>();
                if (requestBody == null)
                {
                    return Results.BadRequest(new { error = "Invalid request body." });
                }

                // Retrieve saved places for this employee
                List<Place> places;
                lock (savedPlaces)
                {
                    if (!savedPlaces.TryGetValue(strippedPass, out places) || places.Count == 0)
                    {
                        return Results.Json(new { error = "No saved places found." }, statusCode: 404);
                    }
                }

                // Build the email content
                string emailBody = EmailHelper.BuildEmailContent(places, requestBody.Notes);

                // Retrieve recipient email from assigned orders
                var customerOrderInfo = await CustomerOrderAccess.GetAssignedOrders(strippedPass);
                if (!customerOrderInfo.Any())
                {
                    return Results.Json(new { error = "No assigned orders found to retrieve customer email." }, statusCode: 400);
                }

                string customerEmail = customerOrderInfo.First().billing.email;
                if (string.IsNullOrWhiteSpace(customerEmail))
                {
                    return Results.Json(new { error = "Customer email is missing." }, statusCode: 400);
                }

                // Send the email
                bool emailSent = await EmailHelper.SendEmail(_emailServer, _emailAddress, _emailPass, customerEmail, emailBody);
                if (!emailSent)
                {
                    return Results.Json(new { error = "Failed to send email." }, statusCode: 500);
                }

                // Clear saved places after successful email send
                lock (savedPlaces)
                {
                    savedPlaces[strippedPass].Clear();
                }

                return Results.Json(new { message = "Email sent successfully." });
            }
            catch (Exception ex)
            {
                Log.Error($"[EMAIL ERROR] {ex.Message}");
                return Results.Json(new { error = "An internal server error occurred.", details = ex.Message }, statusCode: 500);
            }
        });
    }

    public static void MapPbxRoutes(WebApplication app)
    {
        app.MapGet("/getSIPConfig", (HttpContext http) =>
        {
            if (!http.Request.Headers.TryGetValue("Authorization", out var authHeader))
                return Results.Unauthorized();

            string suppliedKey = StripBearerPrefix(authHeader!);
            var employee = LoginAuth.GetEmployee(suppliedKey);

            if (employee is null)
                return Results.Unauthorized();

            // TODO remove hard coded domain name
            var sipConfig = new
            {
                uri = $"sip:{employee.Ext}@slowcasting.com",
                password = employee.SipPass,
                ws_servers = "wss://slowcasting.com:8089/ws"
            };

            return Results.Json(sipConfig);
        });
    }


    // Strips the "Bearer " prefix from the Authorization header if present.
    private static string StripBearerPrefix(string authHeader)
    {
        return authHeader.StartsWith("Bearer ") ? authHeader.Substring(7) : authHeader;
    }
}
