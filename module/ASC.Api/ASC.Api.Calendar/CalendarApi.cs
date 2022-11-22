/*
 *
 * (c) Copyright Ascensio System Limited 2010-2021
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * http://www.apache.org/licenses/LICENSE-2.0
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/


using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

using ASC.Api.Attributes;
using ASC.Api.Calendar.Attachments;
using ASC.Api.Calendar.BusinessObjects;
using ASC.Api.Calendar.ExternalCalendars;
using ASC.Api.Calendar.iCalParser;
using ASC.Api.Calendar.Notification;
using ASC.Api.Calendar.Wrappers;
using ASC.Api.Impl;
using ASC.Api.Interfaces;
using ASC.Api.Interfaces.ResponseTypes;
using ASC.Api.Routing;
using ASC.Common.Caching;
using ASC.Common.Data;
using ASC.Common.Data.Sql;
using ASC.Common.Logging;
using ASC.Common.Radicale;
using ASC.Common.Security;
using ASC.Common.Utils;
using ASC.Core;
using ASC.Security.Cryptography;
using ASC.Specific;
using ASC.Web.Core.Calendars;
using ASC.Web.Core.Files;
using ASC.Web.Studio.Core;
using ASC.Web.Studio.Utility;

using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

using Newtonsoft.Json.Linq;

using SecurityContext = ASC.Core.SecurityContext;

namespace ASC.Api.Calendar
{
    /// <summary>
    /// Access to the calendars.
    /// </summary>
    public class iCalApiContentResponse : IApiContentResponce
    {
        private readonly Stream _stream;
        private readonly string _fileName;


        public iCalApiContentResponse(Stream stream, string fileName)
        {
            _stream = stream;
            _fileName = fileName;
        }

        #region IApiContentResponce Members

        public Encoding ContentEncoding
        {
            get { return Encoding.UTF8; }
        }

        public Stream ContentStream
        {
            get { return _stream; }
        }

        public System.Net.Mime.ContentType ContentType
        {
            get { return new System.Net.Mime.ContentType("text/calendar; charset=UTF-8"); }
        }

        public System.Net.Mime.ContentDisposition ContentDisposition
        {
            get { return new System.Net.Mime.ContentDisposition { Inline = true, FileName = _fileName }; }
        }

        #endregion
    }

    public class ExportDataCache
    {
        public static readonly ICache Cache = AscCache.Default;

        public static String GetCacheKey(string calendarId)
        {
            return String.Format("{0}_ExportCalendar_{1}", TenantProvider.CurrentTenantID, calendarId);
        }

        public static string Get(string calendarId)
        {
            return Cache.Get<string>(GetCacheKey(calendarId));
        }

        public static void Insert(string calendarId, string data)
        {
            if (string.IsNullOrEmpty(data))
                Reset(calendarId);
            else
                Cache.Insert(GetCacheKey(calendarId), data, TimeSpan.FromMinutes(5));
        }

        public static void Reset(string calendarId)
        {
            Cache.Remove(GetCacheKey(calendarId));
        }
    }

    public class CalendarApi : IApiEntryPoint, IDisposable
    {
        public static bool IsPersonal
        {
            get
            {
                return String.Equals(ConfigurationManagerExtension.AppSettings["web.personal"] ?? "false", "true");
            }
        }

        #region IApiEntryPoint Members

        public string Name
        {
            get { return "calendar"; }
        }

        #endregion
        private static readonly List<String> updatedEvents = new List<string>();
        private readonly ApiContext _context;
        private const int _monthCount = 3;
        protected DataProvider _dataProvider;
        private static readonly ILog Logger = LogManager.GetLogger("ASC.Calendar");

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="context"></param>
        public CalendarApi(ApiContext context)
        {
            _context = context;
            CalendarManager.Instance.RegistryCalendar(new SharedEventsCalendar());

            var birthdayReminderCalendar = new BirthdayReminderCalendar();
            if (CoreContext.UserManager.IsUserInGroup(SecurityContext.CurrentAccount.ID, Core.Users.Constants.GroupVisitor.ID))
            {
                CalendarManager.Instance.UnRegistryCalendar(birthdayReminderCalendar.Id);
            }
            else
            {
                CalendarManager.Instance.RegistryCalendar(birthdayReminderCalendar);
            }

            _dataProvider = new DataProvider();
        }

        private CalendarApi()
        {
        }

        #region Calendars & Subscriptions

        /// <summary>
        /// Returns a list of all the event days from one period till another.
        /// </summary>
        /// <short>
        /// Get event days
        /// </short>
        /// <param name="startDate">Period start date</param>
        /// <param name="endDate">Period end date</param>
        /// <category>Events</category>
        /// <returns>List of dates</returns>
        /// <visible>false</visible>
        [Read("eventdays/{startDate}/{endDate}")]
        public List<ApiDateTime> GetEventDays(ApiDateTime startDate, ApiDateTime endDate)
        {
            var result = new List<CalendarWrapper>();
            int newCalendarsCount;
            //internal
            var calendars = _dataProvider.LoadCalendarsForUser(SecurityContext.CurrentAccount.ID, out newCalendarsCount);

            result.AddRange(calendars.ConvertAll(c => new CalendarWrapper(c)));

            if (!IsPersonal)
            {
                //external
                var extCalendars = CalendarManager.Instance.GetCalendarsForUser(SecurityContext.CurrentAccount.ID);
                var viewSettings = _dataProvider.GetUserViewSettings(SecurityContext.CurrentAccount.ID, extCalendars.ConvertAll(c => c.Id));

                var extCalendarsWrappers = extCalendars.ConvertAll(c =>
                                        new CalendarWrapper(c, viewSettings.Find(o => o.CalendarId.Equals(c.Id, StringComparison.InvariantCultureIgnoreCase))))
                                        .FindAll(c => c.IsAcceptedSubscription);


                extCalendarsWrappers.ForEach(c => c.Events = c.UserCalendar.GetEventWrappers(SecurityContext.CurrentAccount.ID, startDate, endDate));
                var sharedEvents = extCalendarsWrappers.Find(c => String.Equals(c.Id, SharedEventsCalendar.CalendarId, StringComparison.InvariantCultureIgnoreCase));


                if (sharedEvents != null)
                    result.ForEach(c =>
                    {
                        c.Events = c.UserCalendar.GetEventWrappers(SecurityContext.CurrentAccount.ID, startDate, endDate);
                        c.Events.RemoveAll(e => sharedEvents.Events.Exists(sEv => string.Equals(sEv.Id, e.Id, StringComparison.InvariantCultureIgnoreCase)));
                    });
                else
                    result.ForEach(c => c.Events = c.UserCalendar.GetEventWrappers(SecurityContext.CurrentAccount.ID, startDate, endDate));

                result.AddRange(extCalendarsWrappers);
            }
            else
            {
                //remove all subscription except ical streams
                result.RemoveAll(c => c.IsSubscription && !c.IsiCalStream);

                result.ForEach(c => c.Events = c.UserCalendar.GetEventWrappers(SecurityContext.CurrentAccount.ID, startDate, endDate));
            }

            var days = new List<ApiDateTime>();
            foreach (var cal in result)
            {
                if (cal.IsHidden)
                    continue;

                foreach (var e in cal.Events)
                {
                    var d = (e.Start.UtcTime + e.Start.TimeZoneOffset).Date;
                    var dend = (e.End.UtcTime + e.End.TimeZoneOffset).Date;
                    while (d <= dend)
                    {
                        if (!days.Exists(day => day == d))
                            days.Add(new ApiDateTime(d, TimeZoneInfo.Utc));

                        d = d.AddDays(1);
                    }

                }
            }

            return days;
        }

        /// <summary>
        /// Returns a list of calendars with the events for the current user in the selected period.
        /// </summary>
        /// <short>
        /// Get calendars
        /// </short>
        /// <param name="startDate">Period start date</param>
        /// <param name="endDate">Period end date</param>
        /// <category>Calendars and subscriptions</category>
        /// <returns>List of calendars with events</returns>
        [Read("calendars/{startDate}/{endDate}")]
        public List<CalendarWrapper> LoadCalendars(ApiDateTime startDate, ApiDateTime endDate)
        {
            int newCalendarsCount;
            var calendars = _dataProvider.LoadCalendarsForUser(SecurityContext.CurrentAccount.ID, out newCalendarsCount);

            var isFirstEntry = !calendars.Exists(c => !c.IsiCalStream() && 
                                           !c.Id.Equals(SharedEventsCalendar.CalendarId, StringComparison.InvariantCultureIgnoreCase) && 
                                            c.OwnerId.Equals(SecurityContext.CurrentAccount.ID));

            var result = LoadInternalCalendars(calendars);


            //external
            if (!IsPersonal)
            {
                var extCalendars = CalendarManager.Instance.GetCalendarsForUser(SecurityContext.CurrentAccount.ID);
                var viewSettings = _dataProvider.GetUserViewSettings(SecurityContext.CurrentAccount.ID, extCalendars.ConvertAll(c => c.Id));

                var extCalendarsWrappers = extCalendars.ConvertAll(c =>
                                        new CalendarWrapper(c, viewSettings.Find(o => o.CalendarId.Equals(c.Id, StringComparison.InvariantCultureIgnoreCase))))
                                        .FindAll(c => c.IsAcceptedSubscription);


                extCalendarsWrappers.ForEach(c => c.Events = c.UserCalendar.GetEventWrappers(SecurityContext.CurrentAccount.ID, startDate, endDate));
                var sharedEvents = extCalendarsWrappers.Find(c => String.Equals(c.Id, SharedEventsCalendar.CalendarId, StringComparison.InvariantCultureIgnoreCase));
                if (sharedEvents != null)
                    result.ForEach(c =>
                    {
                        c.Events = c.UserCalendar.GetEventWrappers(SecurityContext.CurrentAccount.ID, startDate, endDate);
                        c.Todos = c.UserCalendar.GetTodoWrappers(SecurityContext.CurrentAccount.ID, startDate, endDate);
                        c.Events.RemoveAll(e => sharedEvents.Events.Exists(sEv => string.Equals(sEv.Id, e.Id, StringComparison.InvariantCultureIgnoreCase)));
                    });
                else
                    result.ForEach(c =>
                    {
                        c.Events = c.UserCalendar.GetEventWrappers(SecurityContext.CurrentAccount.ID, startDate, endDate);
                        c.Todos = c.UserCalendar.GetTodoWrappers(SecurityContext.CurrentAccount.ID, startDate, endDate);
                    });

                result.AddRange(extCalendarsWrappers);
            }
            else
            {
                //remove all subscription except ical streams
                result.RemoveAll(c => c.IsSubscription && !c.IsiCalStream);

                result.ForEach(c => c.Events = c.UserCalendar.GetEventWrappers(SecurityContext.CurrentAccount.ID, startDate, endDate));
            }

            if (isFirstEntry)
            {
                var tenant = CoreContext.TenantManager.GetCurrentTenant();
                var myUri = HttpContext.Current.Request.GetUrlRewriter();
                var updateNotifications = new Task(() =>
                {
                    CoreContext.TenantManager.SetCurrentTenant(tenant);
                    try
                    {
                        var firstCal = result.FindAll(c => !c.IsSubscription).FirstOrDefault();
                        var firstCalResult = GetCalendarCalDavUrl(firstCal.Id, myUri).Result;
                        var extCalendars = CalendarManager.Instance.GetCalendarsForUser(SecurityContext.CurrentAccount.ID);
                        foreach (var calendar in extCalendars)
                        {
                            var extCalResult = GetCalendarCalDavUrl(calendar.Id, myUri).Result;
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e.Message);
                    }

                }, TaskCreationOptions.LongRunning);

                updateNotifications.ConfigureAwait(false);
                updateNotifications.Start();
                
            }

            return result;

        }

        private List<CalendarWrapper> LoadInternalCalendars(List<BusinessObjects.Calendar> userCalendars = null)
        {
            var result = new List<CalendarWrapper>();
            int newCalendarsCount;
            //internal
            var calendars = userCalendars != null ? userCalendars : _dataProvider.LoadCalendarsForUser(SecurityContext.CurrentAccount.ID, out newCalendarsCount);

            var userTimeZone = CoreContext.TenantManager.GetCurrentTenant().TimeZone;

            result.AddRange(calendars.ConvertAll(c => new CalendarWrapper(c)));

            foreach (var calendarWrapper in result.ToList())
            {
                if (calendarWrapper.Owner.Id != SecurityContext.CurrentAccount.ID)
                {

                    var ownerViewSettings = _dataProvider.GetUserViewSettings(calendarWrapper.Owner.Id, new List<string>() { calendarWrapper.Id });

                    var userViewSettings = _dataProvider.GetUserViewSettings(SecurityContext.CurrentAccount.ID, new List<string>() { calendarWrapper.Id });

                    if (ownerViewSettings.FirstOrDefault() != null && userViewSettings.FirstOrDefault().TimeZone == null)
                        userViewSettings.FirstOrDefault().TimeZone = ownerViewSettings.FirstOrDefault().TimeZone;

                    var newCal = new CalendarWrapper(calendarWrapper.UserCalendar, userViewSettings.FirstOrDefault());

                    result.Remove(calendarWrapper);
                    result.Add(newCal);
                }
            }

            if (!result.Exists(c => !c.IsSubscription))
            {
                //create first calendar
                var firstCal = _dataProvider.CreateCalendar(SecurityContext.CurrentAccount.ID,
                        Resources.CalendarApiResource.DefaultCalendarName, "", BusinessObjects.Calendar.DefaultTextColor, BusinessObjects.Calendar.DefaultBackgroundColor, userTimeZone, EventAlertType.FifteenMinutes, null, new List<SharingOptions.PublicItem>(), new List<UserViewSettings>(), Guid.Empty);

                result.Add(new CalendarWrapper(firstCal));
            }
            return result;
        }

        /// <summary>
        /// Returns a list of all the subscriptions available to the current user.
        /// </summary>
        /// <short>
        /// Get subscriptions
        /// </short>
        /// <category>Calendars and subscriptions</category>
        /// <returns>List of subscriptions</returns>
        [Read("subscriptions")]
        public List<SubscriptionWrapper> LoadSubscriptions()
        {
            var result = new List<SubscriptionWrapper>();

            if (!IsPersonal)
            {

                var calendars = _dataProvider.LoadSubscriptionsForUser(SecurityContext.CurrentAccount.ID);
                result.AddRange(calendars.FindAll(c => !c.OwnerId.Equals(SecurityContext.CurrentAccount.ID)).ConvertAll(c => new SubscriptionWrapper(c)));

                var iCalStreams = _dataProvider.LoadiCalStreamsForUser(SecurityContext.CurrentAccount.ID);
                result.AddRange(iCalStreams.ConvertAll(c => new SubscriptionWrapper(c)));


                var extCalendars = CalendarManager.Instance.GetCalendarsForUser(SecurityContext.CurrentAccount.ID);
                var viewSettings = _dataProvider.GetUserViewSettings(SecurityContext.CurrentAccount.ID, extCalendars.ConvertAll(c => c.Id));

                result.AddRange(extCalendars.ConvertAll(c =>
                                        new SubscriptionWrapper(c, viewSettings.Find(o => o.CalendarId.Equals(c.Id, StringComparison.InvariantCultureIgnoreCase)))));


            }
            else
            {
                var iCalStreams = _dataProvider.LoadiCalStreamsForUser(SecurityContext.CurrentAccount.ID);
                result.AddRange(iCalStreams.ConvertAll(c => new SubscriptionWrapper(c)));
            }

            return result;
        }

        public class SubscriptionState
        {
            public string id { get; set; }
            public bool isAccepted { get; set; }
        }

        /// <summary>
        /// Updates the subscription states either subscribing or unsubscribing the user to/from it.
        /// </summary>
        /// <short>
        /// Update the subscription states
        /// </short>
        /// <param name="states">New subscription states</param>
        /// <category>Calendars and subscriptions</category>
        /// <visible>false</visible>
        [Update("subscriptions/manage")]
        public void ManageSubscriptions(IEnumerable<SubscriptionState> states)
        {
            var viewSettings = _dataProvider.GetUserViewSettings(SecurityContext.CurrentAccount.ID, states.Select(s => s.id).ToList());

            var settingsCollection = new List<UserViewSettings>();
            foreach (var s in states)
            {
                var settings = viewSettings.Find(vs => vs.CalendarId.Equals(s.id, StringComparison.InvariantCultureIgnoreCase));
                if (settings == null)
                {
                    settings = new UserViewSettings
                    {
                        CalendarId = s.id,
                        UserId = SecurityContext.CurrentAccount.ID
                    };
                }
                settings.IsAccepted = s.isAccepted;
                settingsCollection.Add(settings);

            }
            _dataProvider.UpdateCalendarUserView(settingsCollection);
        }

        /// <summary>
        /// Returns the detailed information about a calendar with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Get a calendar by ID
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <category>Calendars and subscriptions</category>
        /// <returns>Calendar</returns>
        [Read("{calendarId}")]
        public CalendarWrapper GetCalendarById(string calendarId)
        {
            int calId;
            if (int.TryParse(calendarId, out calId))
            {
                var cal = _dataProvider.GetCalendarById(calId);
                return (cal != null ? new CalendarWrapper(cal) : null);
            }

            //external                
            var extCalendar = CalendarManager.Instance.GetCalendarForUser(SecurityContext.CurrentAccount.ID, calendarId);
            if (extCalendar != null)
            {
                var viewSettings = _dataProvider.GetUserViewSettings(SecurityContext.CurrentAccount.ID, new List<string> { calendarId });
                return new CalendarWrapper(extCalendar, viewSettings.FirstOrDefault());
            }

            return null;
        }

        public class SharingParam : SharingOptions.PublicItem
        {
            public string actionId { get; set; }
            public Guid itemId
            {
                get { return Id; }
                set { Id = value; }
            }
            public bool isGroup
            {
                get { return IsGroup; }
                set { IsGroup = value; }
            }
        }

        /// <summary>
        /// Creates a new calendar with the parameters (name, description, color, etc.) specified in the request.
        /// </summary>
        /// <short>
        /// Create a calendar
        /// </short>
        /// <param name="name">Calendar name</param>
        /// <param name="description">Calendar description</param>
        /// <param name="textColor">Event text color</param>
        /// <param name="backgroundColor">Event background color</param>
        /// <param name="timeZone">Calendar time zone</param>
        /// <param name="alertType">Event alert type, in case alert type is set by default</param>
        /// <param name="sharingOptions">Calendar sharing options with other users</param>
        /// <param name="iCalUrl">iCal URL</param>
        /// <param name="isTodo">Defines if this calendar is for the todo list</param>
        /// <category>Calendars and subscriptions</category>
        /// <returns>Created calendar</returns>
        [Create("")]
        public async Task<CalendarWrapper> CreateCalendar(string name, string description, string textColor, string backgroundColor, string timeZone, EventAlertType alertType, List<SharingParam> sharingOptions, string iCalUrl, int isTodo = 0)
        {
            var sharingOptionsList = sharingOptions ?? new List<SharingParam>();
            var timeZoneInfo = TimeZoneConverter.GetTimeZone(timeZone);

            name = (name ?? "").Trim();
            if (String.IsNullOrEmpty(name))
                throw new Exception(Resources.CalendarApiResource.ErrorEmptyName);

            description = (description ?? "").Trim();
            textColor = (textColor ?? "").Trim();
            backgroundColor = (backgroundColor ?? "").Trim();

            Guid calDavGuid = Guid.NewGuid();
            var myUri = HttpContext.Current.Request.GetUrlRewriter();
            var currentUser = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID);
            var currentUserEmail = CheckUserEmail(currentUser) ? CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID).Email : null;
            var currentUserName = currentUserEmail != null ? currentUserEmail.ToLower() + "@" + myUri.Host : null;
            var tenant = CoreContext.TenantManager.GetCurrentTenant();

            await CreateCalDavCalendar(name, description, backgroundColor, calDavGuid.ToString(), myUri, currentUserName, currentUserEmail).ConfigureAwait(false);
            CoreContext.TenantManager.SetCurrentTenant(tenant.TenantId);
            var cal = _dataProvider.CreateCalendar(
                        SecurityContext.CurrentAccount.ID, name, description, textColor, backgroundColor, timeZoneInfo, alertType, null,
                        sharingOptionsList.Select(o => o as SharingOptions.PublicItem).ToList(),
                        new List<UserViewSettings>(), calDavGuid, isTodo);

            if (cal == null) throw new Exception("calendar is null");

            foreach (var opt in sharingOptionsList)
                if (String.Equals(opt.actionId, AccessOption.FullAccessOption.Id, StringComparison.InvariantCultureIgnoreCase))
                    CoreContext.AuthorizationManager.AddAce(new AzRecord(opt.Id, CalendarAccessRights.FullAccessAction.ID, Common.Security.Authorizing.AceType.Allow, cal));

            //notify
            CalendarNotifyClient.NotifyAboutSharingCalendar(cal);

            //iCalUrl
            if (!string.IsNullOrEmpty(iCalUrl))
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(iCalUrl);
                    using (var resp = req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    {
                        var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        ms.Seek(0, SeekOrigin.Begin);

                        using (var tempReader = new StreamReader(ms))
                        {

                            var cals = DDayICalParser.DeserializeCalendar(tempReader);
                            await ImportEvents(Convert.ToInt32(cal.Id), cals).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info(String.Format("Error import events to new calendar by ical url: {0}", ex.Message));
                }

            }

            return new CalendarWrapper(cal);
        }


        private async Task UpdateSharedCalDavCalendarAsync(DataProvider dataProvider, string name, string description, string backgroundColor, string calDavGuid, Uri myUri, List<SharingParam> sharingOptionsList, List<IEvent> events, string calendarId, string calendarGuid, int tenantId, DateTime updateDate = default(DateTime),
                            VTimeZone calendarVTimeZone = null,
                            TimeZoneInfo calendarTimeZone = null)
        {
            try
            {
                CoreContext.TenantManager.SetCurrentTenant(tenantId);

                var calendarIcs = GetCalendariCalString(dataProvider, calendarId);
                var parseCalendar = DDayICalParser.DeserializeCalendar(calendarIcs);
                var calendar = parseCalendar.FirstOrDefault();


                foreach (var sharingParam in sharingOptionsList)
                {
                    var fullAccess = sharingParam.actionId == AccessOption.FullAccessOption.Id ||
                                                 sharingParam.actionId == AccessOption.OwnerOption.Id;

                    CoreContext.TenantManager.SetCurrentTenant(tenantId);
                    if (sharingParam.isGroup)
                    {
                        var updateGroupTask = new List<Task>();
                        var users = CoreContext.UserManager.GetUsersByGroup(sharingParam.itemId);

                        foreach (var userGroup in users)
                        {
                            updateGroupTask.Add(UpdateCalDavCalendar(name, description, backgroundColor, calDavGuid, myUri, userGroup.Email.ToLower(), true));

                            foreach (var e in events)
                            {

                                var evt = DDayICalParser.ConvertEvent(e as BaseEvent, calendarTimeZone);
                                if (evt == null) continue;
                                var uid = evt.Uid;
                                string[] split = uid.Split(new Char[] { '@' });
                                evt.Uid = split[0];

                                calendar.Events.Clear();
                                calendar.Events.Add(evt);

                                var ics = DDayICalParser.SerializeCalendar(calendar);

                                updateGroupTask.Add(UpdateSharedEvent(userGroup, evt.Uid, fullAccess, myUri, ics, calendarGuid, updateDate, calendarVTimeZone, calendarTimeZone));
                            }
                        }
                        await Task.WhenAll(updateGroupTask).ConfigureAwait(false);
                    }
                    else
                    {
                        var user = CoreContext.UserManager.GetUsers(sharingParam.itemId);
                        var updateUserTasks = new List<Task>();

                        if (!CheckUserEmail(user)) return;

                        updateUserTasks.Add(UpdateCalDavCalendar(name, description, backgroundColor, calDavGuid, myUri, user.Email.ToLower(), true));

                        foreach (var sharedEvent in events)
                        {
                            var evt = DDayICalParser.ConvertEvent(sharedEvent as BaseEvent, calendarTimeZone);
                            if (evt == null) continue;

                            var uid = evt.Uid;
                            string[] split = uid.Split(new Char[] { '@' });
                            evt.Uid = split[0];

                            calendar.Events.Clear();
                            calendar.Events.Add(evt);

                            var ics = DDayICalParser.SerializeCalendar(calendar);

                            updateUserTasks.Add(UpdateSharedEvent(user, evt.Uid, fullAccess, myUri, ics, calendarGuid, updateDate, calendarVTimeZone, calendarTimeZone));
                        }

                        await Task.WhenAll(updateUserTasks).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("ERROR: " + ex.Message);
            }

        }




        /// <summary>
        /// Updates the selected calendar with the parameters (name, description, color, etc.) specified in the request.
        /// </summary>
        /// <short>
        /// Update a calendar
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="name">New calendar name</param>
        /// <param name="description">New calendar description</param>
        /// <param name="textColor">New event text color</param>
        /// <param name="backgroundColor">New event background color</param>
        /// <param name="timeZone">New calendar time zone</param>
        /// <param name="alertType">New event alert type, in case alert type is set by default</param>
        /// <param name="hideEvents">Display type: show or hide events in the calendar</param>
        /// <param name="sharingOptions">New calendar sharing options with other users</param>
        /// <param name="iCalUrl">New iCal URL</param>
        /// <category>Calendars and subscriptions</category>
        /// <returns>Updated calendar</returns>
        [Update("{calendarId}")]
        public async Task<CalendarWrapper> UpdateCalendar(string calendarId, string name, string description, string textColor, string backgroundColor, string timeZone, EventAlertType alertType, bool hideEvents, List<SharingParam> sharingOptions, string iCalUrl = "")
        {
            TimeZoneInfo timeZoneInfo = TimeZoneConverter.GetTimeZone(timeZone);
            int calId;
            var currentTenantId = CoreContext.TenantManager.GetCurrentTenant().TenantId;
            var myUri = HttpContext.Current.Request.GetUrlRewriter();
            if (!string.IsNullOrEmpty(iCalUrl))
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(iCalUrl);
                    using (var resp = req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    {
                        var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        ms.Seek(0, SeekOrigin.Begin);

                        using (var tempReader = new StreamReader(ms))
                        {

                            var cals = DDayICalParser.DeserializeCalendar(tempReader);
                            await ImportEvents(Convert.ToInt32(calendarId), cals).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info(String.Format("Error import events to calendar by ical url: {0}", ex.Message));
                }

            }

            CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
            if (int.TryParse(calendarId, out calId))
            {
                var oldCal = _dataProvider.GetCalendarById(calId);
                if (CheckPermissions(oldCal, CalendarAccessRights.FullAccessAction, true))
                {
                    //update calendar and share options
                    var sharingOptionsList = sharingOptions ?? new List<SharingParam>();

                    name = (name ?? "").Trim();
                    if (String.IsNullOrEmpty(name))
                        throw new Exception(Resources.CalendarApiResource.ErrorEmptyName);

                    description = (description ?? "").Trim();
                    textColor = (textColor ?? "").Trim();
                    backgroundColor = (backgroundColor ?? "").Trim();


                    //view
                    var userOptions = oldCal.ViewSettings;
                    var usrOpt = userOptions.Find(o => o.UserId.Equals(SecurityContext.CurrentAccount.ID));
                    if (usrOpt == null)
                    {
                        userOptions.Add(new UserViewSettings
                        {
                            Name = name,
                            TextColor = textColor,
                            BackgroundColor = backgroundColor,
                            EventAlertType = alertType,
                            IsAccepted = true,
                            UserId = SecurityContext.CurrentAccount.ID,
                            TimeZone = timeZoneInfo
                        });
                    }
                    else
                    {
                        usrOpt.Name = name;
                        usrOpt.TextColor = textColor;
                        usrOpt.BackgroundColor = backgroundColor;
                        usrOpt.EventAlertType = alertType;
                        usrOpt.TimeZone = timeZoneInfo;
                    }

                    userOptions.RemoveAll(o => !o.UserId.Equals(oldCal.OwnerId) && !sharingOptionsList.Exists(opt => (!opt.IsGroup && o.UserId.Equals(opt.Id))
                                                                               || opt.IsGroup && CoreContext.UserManager.IsUserInGroup(o.UserId, opt.Id)));

                    //check owner
                    if (!oldCal.OwnerId.Equals(SecurityContext.CurrentAccount.ID))
                    {
                        name = oldCal.Name;
                        description = oldCal.Description;
                    }


                    var _email = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID).Email;

                    var cal = _dataProvider.UpdateCalendar(calId, name, description,
                                        sharingOptionsList.Select(o => o as SharingOptions.PublicItem).ToList(),
                                        userOptions);

                    var oldSharingList = new List<SharingParam>();
                    var owner = CoreContext.UserManager.GetUsers(cal.OwnerId);

                    if (cal != null)
                    {
                        //clear old rights
                        CoreContext.AuthorizationManager.RemoveAllAces(cal); // TODO: an understandable error related to tenant availability 

                        foreach (var opt in sharingOptionsList)
                            if (String.Equals(opt.actionId, AccessOption.FullAccessOption.Id, StringComparison.InvariantCultureIgnoreCase))
                                CoreContext.AuthorizationManager.AddAce(new AzRecord(opt.Id, CalendarAccessRights.FullAccessAction.ID, Common.Security.Authorizing.AceType.Allow, cal));

                        //notify
                        CalendarNotifyClient.NotifyAboutSharingCalendar(cal, oldCal);

                        if (SecurityContext.CurrentAccount.ID != cal.OwnerId)
                        {
                            if (CheckUserEmail(owner))
                            {
                                await UpdateCalDavCalendar(name, description, backgroundColor, oldCal.calDavGuid, myUri, owner.Email);
                            }
                        }
                        else
                        {
                            await UpdateCalDavCalendar(name, description, backgroundColor, oldCal.calDavGuid, myUri, _email);
                        }
                        var pic = PublicItemCollection.GetForCalendar(oldCal);
                        if (pic.Items.Count > 1)
                        {
                            oldSharingList.AddRange(from publicItem in pic.Items
                                                    where publicItem.ItemId != owner.ID.ToString()
                                                    select new SharingParam
                                                    {
                                                        Id = Guid.Parse(publicItem.ItemId),
                                                        isGroup = publicItem.IsGroup,
                                                        actionId = publicItem.SharingOption.Id
                                                    });
                        }
                        if (sharingOptionsList.Count > 0)
                        {
                            var tenant = CoreContext.TenantManager.GetCurrentTenant();
                            var events = cal.LoadEvents(SecurityContext.CurrentAccount.ID, DateTime.MinValue, DateTime.MaxValue);
                            var calendarObjViewSettings = cal != null && cal.ViewSettings != null ? cal.ViewSettings.FirstOrDefault() : null;
                            var targetCalendar = DDayICalParser.ConvertCalendar(cal != null ? cal.GetUserCalendar(calendarObjViewSettings) : null);

                            using (var dataProvider = new DataProvider())
                            {
                                await UpdateSharedCalDavCalendarAsync(dataProvider, name, description, backgroundColor, oldCal.calDavGuid, myUri, sharingOptionsList, events, calendarId, cal.calDavGuid, tenant.TenantId, DateTime.Now, targetCalendar.TimeZones[0], cal.TimeZone)
                                    .ConfigureAwait(false);
                            }


                        }

                        oldSharingList.RemoveAll(c => sharingOptionsList.Contains(sharingOptionsList.Find((x) => x.Id == c.Id)));

                        if (oldSharingList.Count > 0)
                        {
                            await ReplaceUpdateCalDavSharingEvent(oldSharingList, myUri, cal).ConfigureAwait(false);
                        }

                        return new CalendarWrapper(cal);
                    }

                    return null;
                }
            }

            //update view
            return UpdateCalendarView(calendarId, name, textColor, backgroundColor, timeZone, alertType, hideEvents);

        }





        /// <summary>
        /// Updates the calendar display parameters specified in the request for the current user.
        /// </summary>
        /// <short>
        /// Update the calendar view
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="name">New calendar name</param>
        /// <param name="textColor">New event text color</param>
        /// <param name="backgroundColor">New event background color</param>
        /// <param name="timeZone">New calendar time zone</param>
        /// <param name="alertType">New event alert type, in case alert type is set by default</param>
        /// <param name="hideEvents">Display type: show or hide events in calendar</param>
        /// <category>Calendars and subscriptions</category>
        /// <returns>Updated calendar</returns>
        [Update("{calendarId}/view")]
        public CalendarWrapper UpdateCalendarView(string calendarId, string name, string textColor, string backgroundColor, string timeZone, EventAlertType alertType, bool hideEvents)
        {
            TimeZoneInfo timeZoneInfo = TimeZoneConverter.GetTimeZone(timeZone);
            name = (name ?? "").Trim();
            if (String.IsNullOrEmpty(name))
                throw new Exception(Resources.CalendarApiResource.ErrorEmptyName);

            var settings = new UserViewSettings
            {
                BackgroundColor = backgroundColor,
                CalendarId = calendarId,
                IsHideEvents = hideEvents,
                TextColor = textColor,
                EventAlertType = alertType,
                IsAccepted = true,
                UserId = SecurityContext.CurrentAccount.ID,
                Name = name,
                TimeZone = timeZoneInfo
            };

            _dataProvider.UpdateCalendarUserView(settings);
            return GetCalendarById(calendarId);
        }

        /// <summary>
        /// Deletes a calendar with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Delete a calendar
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <category>Calendars and subscriptions</category>
        [Delete("{calendarId}")]
        public async Task RemoveCalendar(int calendarId)
        {
            var cal = _dataProvider.GetCalendarById(calendarId);
            var events = cal.LoadEvents(SecurityContext.CurrentAccount.ID, DateTime.MinValue, DateTime.MaxValue);

            var pic = PublicItemCollection.GetForCalendar(cal);
            //check permissions
            CheckPermissions(cal, CalendarAccessRights.FullAccessAction);
            //clear old rights
            CoreContext.AuthorizationManager.RemoveAllAces(cal);
            var caldavGuid = _dataProvider.RemoveCalendar(calendarId);

            var eventIds = events.Select(x => x.Id);
            AttachmentEngine.DeleteFolders(eventIds);

            var myUri = HttpContext.Current.Request.GetUrlRewriter();
            var currentTenantId = TenantProvider.CurrentTenantID;
            var caldavHost = myUri.Host;
            if (caldavGuid != Guid.Empty)
            {
                Logger.Info("RADICALE REWRITE URL: " + myUri);

                var currentUser = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID);
                var currentUserEmail = CheckUserEmail(currentUser) ? CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID).Email : null;
                var currentUserName = currentUserEmail != null ? currentUserEmail.ToLower() + "@" + caldavHost : null;

                if (currentUserEmail != null)
                {
                    try
                    {
                        await RemoveCaldavCalendar(currentUserEmail, caldavGuid.ToString(), myUri).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
                    }
                }
            }

            var sharingList = new List<SharingParam>();
            CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
            if (pic.Items.Count > 1)
            {

                sharingList.AddRange(from publicItem in pic.Items
                                     where publicItem.ItemId != SecurityContext.CurrentAccount.ID.ToString()
                                     select new SharingParam
                                     {
                                         Id = Guid.Parse(publicItem.ItemId),
                                         isGroup = publicItem.IsGroup,
                                         actionId = publicItem.SharingOption.Id
                                     });
            }

            await ReplaceRemoveSharingEventTask(sharingList, myUri, cal, events).ConfigureAwait(false);

        }

        #endregion


        #region Caldav/methods

        private async Task UpdateCalDavCalendar(string name, string description, string backgroundColor, string calDavGuid, Uri myUri, string email, bool isSharedCalendar = false)
        {
            var CalDavCalendar = new CalDavCalendar(calDavGuid, isSharedCalendar);

            name = (name ?? "").Trim();
            if (String.IsNullOrEmpty(name))
                throw new Exception(Resources.CalendarApiResource.ErrorEmptyName);

            description = (description ?? "").Trim();
            backgroundColor = (backgroundColor ?? "").Trim();

            var authorization = isSharedCalendar ? CalDavCalendar.GetSystemAuthorization() : GetUserAuthorization(email);


            await CalDavCalendar.Update(name, description, backgroundColor, myUri, email.ToLower(), authorization).ConfigureAwait(false);
        }

        private async Task<DavResponse> CreateCalDavCalendar(string name, string description, string backgroundColor, string calDavGuid, Uri myUri, string currentUserName, string email, bool isSharedCalendar = false)
        {
            var CalDavCalendar = new CalDavCalendar(calDavGuid, isSharedCalendar);

            name = (name ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                throw new Exception(Resources.CalendarApiResource.ErrorEmptyName);

            description = (description ?? "").Trim();
            backgroundColor = (backgroundColor ?? "").Trim();

            var authorization = isSharedCalendar ? CalDavCalendar.GetSystemAuthorization() : GetUserAuthorization(email);
            return await CalDavCalendar.CreateAsync(name, description, backgroundColor, myUri, currentUserName, authorization).ConfigureAwait(false);


        }

        private static async Task RemoveCaldavCalendar(string email, string calDavGuid, Uri myUri, bool isShared = false)
        {

            try
            {
                var calDavCalendar = new CalDavCalendar(calDavGuid, isShared);
                var requestUrl = calDavCalendar.GetRadicaleUrl(myUri.ToString(), email, isShared, isRedirectUrl: true, entityId: calDavGuid);
                var authorization = isShared ? calDavCalendar.GetSystemAuthorization() : GetUserAuthorization(email);
                var davRequest = new DavRequest()
                {
                    Url = requestUrl,
                    Authorization = authorization
                };
                await RadicaleClient.RemoveAsync(davRequest).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private async Task CreateCaldavEvents(string calDavGuid, Uri myUri, string currentUserEmail, BaseCalendar icalendar, string calendarIcs, int tenantId)
        {
            var parseCalendar = DDayICalParser.DeserializeCalendar(calendarIcs);
            var calendar = parseCalendar.FirstOrDefault();
            CoreContext.TenantManager.SetCurrentTenant(tenantId);

            var calendarId = icalendar.Id;
            var ddayCalendar = new Ical.Net.Calendar();
            try
            {
                if (calendar != null)
                {
                    var events = calendar.Events;
                    var updateCaldavEventTasks = new List<Task>();
                    foreach (var evt in events)
                    {
                        var uid = evt.Uid;
                        if (evt.Created != null)
                            evt.Created = DDayICalParser.ToUtc(evt.Created) != DateTime.MinValue ? evt.Created : new CalDateTime(DateTime.Now);
                        string[] split = uid.Split(new Char[] { '@' });
                        ddayCalendar = DDayICalParser.ConvertCalendar(icalendar);
                        ddayCalendar.Events.Clear();
                        ddayCalendar.Events.Add(evt);

                        var ics = DDayICalParser.SerializeCalendar(ddayCalendar);
                        updateCaldavEventTasks.Add(UpdateCaldavEventTask(ics, split[0], true, calDavGuid, myUri, currentUserEmail,
                                            DateTime.Now, ddayCalendar.TimeZones[0], icalendar.TimeZone));

                    }
                    var todos = icalendar.GetTodoWrappers(SecurityContext.CurrentAccount.ID, new ApiDateTime(DateTime.MinValue, icalendar.TimeZone), new ApiDateTime(DateTime.MaxValue, icalendar.TimeZone));
                    foreach (var td in todos)
                    {
                        ddayCalendar = DDayICalParser.ConvertCalendar(icalendar);
                        ddayCalendar.Todos.Clear();

                        var todo = new Ical.Net.CalendarComponents.Todo
                        {
                            Summary = td.Name,
                            Description = td.Description,
                            Start = td.Start != DateTime.MinValue ? new CalDateTime(td.Start) : null,
                            Completed = td.Completed != DateTime.MinValue ? new CalDateTime(td.Completed) : null,
                        };

                        ddayCalendar.Todos.Add(todo);

                        var ics = DDayICalParser.SerializeCalendar(ddayCalendar);
                        var uid = td.Uid;
                        string[] split = uid.Split(new Char[] { '@' });
                        updateCaldavEventTasks.Add(UpdateCaldavEventTask(ics, split[0], true, calDavGuid, myUri, currentUserEmail,
                                            DateTime.Now, ddayCalendar.TimeZones[0], icalendar.TimeZone));
                    }
                    await Task.WhenAll(updateCaldavEventTasks).ConfigureAwait(false);
                }

            }
            catch (Exception exception)
            {
                Logger.Error("ERROR. Create caldav events: " + exception.Message);
            }

        }

        private static async Task UpdateCaldavEventTask(
                            string ics,
                            string uid,
                            bool sendToRadicale,
                            string guid,
                            Uri myUri,
                            string userEmail,
                            DateTime updateDate = default(DateTime),
                            VTimeZone calendarVTimeZone = null,
                            TimeZoneInfo calendarTimeZone = null,
                            bool isDelete = false,
                            bool isShared = false
            )
        {
            if (sendToRadicale)
            {
                try
                {
                    if (guid != null && guid != "")
                    {

                        var calDavServerUrl = myUri.Scheme + "://" + myUri.Host + "/caldav";
                        var caldavHost = myUri.Host;

                        Logger.Info("RADICALE REWRITE URL: " + myUri);

                        if (userEmail == null) return;

                        var currentUserName = userEmail.ToLower() + "@" + caldavHost;

                        int indexOfChar = ics.IndexOf("BEGIN:VTIMEZONE");
                        int indexOfCharEND = ics.IndexOf("END:VTIMEZONE");

                        if (indexOfChar != -1)
                        {
                            ics = ics.Remove(indexOfChar, indexOfCharEND + 14 - indexOfChar);
                            if (ics.IndexOf("BEGIN:VTIMEZONE") > -1) await UpdateCaldavEventTask(ics, uid, true, guid, myUri, userEmail).ConfigureAwait(false);
                        }


                        var icsCalendars = DDayICalParser.DeserializeCalendar(ics);
                        var icsCalendar = icsCalendars == null ? null : icsCalendars.FirstOrDefault();
                        var icsEvents = icsCalendar == null ? null : icsCalendar.Events;
                        var icsEvent = icsEvents == null ? null : icsEvents.FirstOrDefault();

                        var icsTodos = icsCalendar == null ? null : icsCalendar.Todos;
                        var icsTodo = icsTodos == null ? null : icsTodos.FirstOrDefault();

                        if (calendarTimeZone != null && calendarVTimeZone != null)
                        {
                            if (icsEvent != null && !icsEvent.IsAllDay)
                            {
                                icsEvent.Created = null;

                                //var tz = TimeZoneConverter.GetTimeZone(calendarVTimeZone.TzId);

                                //if (icsEvent.DtStart.TzId != calendarVTimeZone.TzId)
                                //{
                                //    var _DtStart = DDayICalParser.ToUtc(icsEvent.Start).Add(tz.GetUtcOffset(icsEvent.Start.Value));
                                //    icsEvent.DtStart = new CalDateTime(_DtStart.Year, _DtStart.Month, _DtStart.Day, _DtStart.Hour, _DtStart.Minute, _DtStart.Second, calendarVTimeZone.TzId);
                                //}

                                //if (icsEvent.DtEnd.TzId != calendarVTimeZone.TzId)
                                //{
                                //    var _DtEnd = DDayICalParser.ToUtc(icsEvent.End).Add(tz.GetUtcOffset(icsEvent.End.Value));
                                //    icsEvent.DtEnd = new CalDateTime(_DtEnd.Year, _DtEnd.Month, _DtEnd.Day, _DtEnd.Hour, _DtEnd.Minute, _DtEnd.Second, calendarVTimeZone.TzId);
                                //}

                                //foreach (var periodList in icsEvent.ExceptionDates)
                                //{
                                //    periodList.Parameters.Add("TZID", calendarVTimeZone.TzId);
                                //}

                            }

                            //icsCalendar.TimeZones.Clear();
                            //icsCalendar.TimeZones.Add(calendarVTimeZone);

                        }
                        if (icsEvent != null)
                        {
                            icsEvent.Uid = uid;
                            if (!isDelete)
                            {
                                icsEvent.ExceptionDates.Clear();
                            }
                        }

                        if (icsTodo != null)
                        {
                            icsTodo.Uid = uid;
                        }

                        ics = DDayICalParser.SerializeCalendar(icsCalendar);

                        try
                        {
                            var calDavCalendar = new CalDavCalendar(guid, isShared);
                            var authorization = isShared ? calDavCalendar.GetSystemAuthorization() : GetUserAuthorization(userEmail);
                            var requestUrl = calDavCalendar.GetRadicaleUrl(myUri.ToString(), userEmail.ToLower(), isShared, false, true, guid, uid);
                            await calDavCalendar.UpdateItem(requestUrl, authorization, ics).ConfigureAwait(false);
                        }
                        catch (WebException ex)
                        {
                            if (ex.Status == WebExceptionStatus.ProtocolError && ex.Response != null)
                            {
                                var resp = (HttpWebResponse)ex.Response;
                                if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Conflict)
                                    Logger.Debug("ERROR: " + ex.Message);
                                else
                                    Logger.Error("ERROR: " + ex.Message);
                            }
                            else
                            {
                                Logger.Error("ERROR: " + ex.Message);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("ERROR: " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("ERROR: " + ex.Message);
                }
            }
        }

        private async Task CreateCaldavSharedEvents(string calendarId, string calendarIcs, Uri myUri, string currentUserEmail, BaseCalendar icalendar, Common.Security.Authentication.IAccount currentUser, int tenantId)
        {
            var parseCalendar = DDayICalParser.DeserializeCalendar(calendarIcs);
            var calendar = parseCalendar.FirstOrDefault();
            CoreContext.TenantManager.SetCurrentTenant(tenantId);
            try
            {
                if (calendar != null)
                {
                    var updateCaldavEventTasks = new List<Task>();
                    var events = new List<CalendarEvent>();
                    var isFullAccess = false;
                    if (calendarId != BirthdayReminderCalendar.CalendarId && calendarId != "crm_calendar" && !calendarId.Contains("Project_"))
                    {
                        foreach (var e in icalendar.LoadEvents(currentUser.ID, DateTime.MinValue, DateTime.MaxValue))
                        {
                            events.Add(DDayICalParser.ConvertEvent(e as BaseEvent, icalendar.TimeZone));
                        }
                    }
                    else
                    {
                        events.AddRange(calendar.Events);
                    }
                    foreach (var e in events)
                    {
                        Event evt = null;
                        using (_dataProvider = new DataProvider())
                        {
                            evt = _dataProvider.GetEventOnlyByUid(e.Uid);
                        }

                        isFullAccess = calendarId != BirthdayReminderCalendar.CalendarId && calendarId != "crm_calendar" ?
                                            evt != null ? SecurityContext.PermissionResolver.Check(currentUser, evt, null, CalendarAccessRights.FullAccessAction) : isFullAccess
                                            : isFullAccess;
                        var uid = e.Uid;
                        string[] split = uid.Split(new Char[] { '@' });
                        e.Uid = split[0];

                        if (calendarId == BirthdayReminderCalendar.CalendarId)
                        {
                            e.Created = null;
                            e.End = new CalDateTime(e.Start.AddDays(1));
                            var evtUid = split[0].Split(new Char[] { '_' });
                            e.Uid = evtUid[1];
                        }
                        else if (calendarId.Contains("Project_"))
                        {
                            e.Created = null;
                            e.End = new CalDateTime(e.End.AddDays(1));
                        }
                        else if (calendarId == "crm_calendar" || calendarId.Contains("Project_"))
                        {
                            e.Created = null;
                            e.Status = EventStatus.Confirmed.ToString();
                        }

                        calendar.Events.Clear();
                        calendar.Events.Add(e);
                        var ics = DDayICalParser.SerializeCalendar(calendar);

                        var eventUid = isFullAccess ? e.Uid + "_write" : e.Uid;
                        updateCaldavEventTasks.Add(UpdateCaldavEventTask(ics, eventUid, true, calendarId,
                                              myUri, currentUserEmail, DateTime.Now,
                                              calendar.TimeZones[0], icalendar.TimeZone, false, true));
                    }
                    await Task.WhenAll(updateCaldavEventTasks).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                Logger.Error("ERROR. Create shared caldav events: " + exception.Message);
            }
        }

        private async Task ReplaceUpdateCalDavSharingEvent(List<SharingParam> list, Uri myUri, BusinessObjects.Calendar cal)
        {
            try
            {
                var removeCaldavCalendarTasks = new List<Task>();
                foreach (var sharingOption in list)
                {
                    if (!sharingOption.IsGroup)
                    {
                        var user = CoreContext.UserManager.GetUsers(sharingOption.itemId);
                        if (CheckUserEmail(user))
                        {
                            var currentUserName = user.Email.ToLower() + "@" + myUri.Host;
                            var userEmail = user.Email;
                            removeCaldavCalendarTasks.Add(RemoveCaldavCalendar(userEmail, cal.calDavGuid, myUri, user.ID != cal.OwnerId));
                        }
                    }
                    else
                    {
                        var users = CoreContext.UserManager.GetUsersByGroup(sharingOption.itemId);
                        foreach (var user in users)
                        {
                            if (CheckUserEmail(user))
                            {
                                var userEmail = user.Email;
                                var currentUserName = userEmail.ToLower() + "@" + myUri.Host;
                                removeCaldavCalendarTasks.Add(RemoveCaldavCalendar(userEmail, cal.calDavGuid, myUri, user.ID != cal.OwnerId));
                            }
                        }
                    }
                }

                await Task.WhenAll(removeCaldavCalendarTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
            }
        }

        private async Task ReplaceRemoveSharingEventTask(List<SharingParam> list, Uri myUri, BusinessObjects.Calendar cal, List<IEvent> events)
        {
            try
            {
                var currentTenant = CoreContext.TenantManager.GetCurrentTenant();
                await ReplaceUpdateCalDavSharingEvent(list, myUri, cal).ConfigureAwait(false);

                CoreContext.TenantManager.SetCurrentTenant(currentTenant.TenantId);


                var deleteEventTasks = new List<Task>();
                foreach (var evt in events)
                {
                    if (evt.SharingOptions.PublicItems.Count > 0)
                    {
                        var permissions = PublicItemCollection.GetForEvent(evt);
                        var so = permissions.Items
                            .Where(x => x.SharingOption.Id != AccessOption.OwnerOption.Id)
                            .Select(x => new SharingParam
                            {
                                Id = x.Id,
                                actionId = x.SharingOption.Id,
                                isGroup = x.IsGroup
                            }).ToList();
                        var uid = evt.Uid;
                        string[] split = uid.Split(new Char[] { '@' });
                        foreach (var sharingOption in so)
                        {
                            var fullAccess = sharingOption.actionId == AccessOption.FullAccessOption.Id;

                            if (!sharingOption.IsGroup)
                            {
                                var user = CoreContext.UserManager.GetUsers(sharingOption.itemId);
                                if (CheckUserEmail(user))
                                {
                                    deleteEventTasks.Add(deleteEvent(fullAccess ? split[0] + "_write" : split[0], SharedEventsCalendar.CalendarId, user.Email, myUri, user.ID != evt.OwnerId));
                                }
                            }
                            else
                            {
                                var users = CoreContext.UserManager.GetUsersByGroup(sharingOption.itemId);
                                foreach (var user in users)
                                {
                                    if (CheckUserEmail(user))
                                    {
                                        var eventUid = user.ID == evt.OwnerId
                                                       ? split[0]
                                                       : fullAccess ? split[0] + "_write" : split[0];

                                        deleteEventTasks.Add(deleteEvent(eventUid, SharedEventsCalendar.CalendarId, user.Email, myUri, true));
                                    }
                                }
                            }
                        }
                        await Task.WhenAll(deleteEventTasks).ConfigureAwait(false);

                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
            }
        }



        private async Task UpdateCalDavEvent(string change, Uri calDavUrl)
        {
            try
            {
                using (_dataProvider = new DataProvider())
                {

                    var serverCalDavUrl = new Uri(calDavUrl.Scheme + "://" + calDavUrl.Host + "/caldav");

                    var eventURl = serverCalDavUrl + "/" + change;

                    var changeData = change.Split('/');

                    var caldavGuid = changeData[1];
                    var eventGuid = changeData[2].Split('.')[0];

                    var sharedPostfixIndex = caldavGuid.IndexOf("-readonly");

                    var calendarId = 0;
                    var ownerId = new Guid();

                    if (sharedPostfixIndex != -1)
                    {
                        int ind = caldavGuid.Length;
                        caldavGuid = caldavGuid.Remove(sharedPostfixIndex, ind - sharedPostfixIndex);

                        var fullAccessPostfixIndex = eventGuid.IndexOf("_write");
                        if (fullAccessPostfixIndex != -1)
                        {
                            eventGuid = eventGuid.Remove(fullAccessPostfixIndex, eventGuid.Length - fullAccessPostfixIndex);
                        }
                    }
                    if (caldavGuid == BirthdayReminderCalendar.CalendarId ||
                        caldavGuid == "crm_calendar") {
                        return;
                    }
                    else if (caldavGuid == SharedEventsCalendar.CalendarId)
                    {
                        var userName = changeData[0];
                        var userData = userName.Split('@');
                        var tenantName = userData[2];
                        try
                        {
                            var tenant = CoreContext.TenantManager.GetTenant(tenantName);
                            if (tenant != null)
                            {
                                var email = string.Join("@", userData[0], userData[1]);
                                CoreContext.TenantManager.SetCurrentTenant(tenant);
                                var user = CoreContext.UserManager.GetUserByEmail(email);

                                var extCalendar = CalendarManager.Instance.GetCalendarForUser(user.ID, caldavGuid);
                                var events = extCalendar.LoadEvents(user.ID, DateTime.MinValue, DateTime.MaxValue);

                                string currentEventId =
                                    (from e in events where e.Uid.Split('@')[0] == eventGuid select e.Id).FirstOrDefault();

                                if (currentEventId != null)
                                {
                                    var evt = _dataProvider.GetEventById(Convert.ToInt32(currentEventId));
                                    calendarId = Convert.ToInt32(evt.CalendarId);
                                    ownerId = Guid.Parse(evt.OwnerId.ToString());
                                }
                                else
                                {
                                    Logger.Error("ERROR: error update calDav event. get current event id");
                                    return;
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            Logger.Error(exception);
                            return;
                        }
                    }
                    else
                    {
                        var calendar = _dataProvider.GetCalendarIdByCaldavGuid(caldavGuid);

                        calendarId = Convert.ToInt32(calendar[0][0]);
                        ownerId = Guid.Parse(calendar[0][1].ToString());
                        CoreContext.TenantManager.SetCurrentTenant(Convert.ToInt32(calendar[0][2]));
                    }

                    var currentUserId = Guid.Empty;
                    if (SecurityContext.IsAuthenticated)
                    {
                        currentUserId = SecurityContext.CurrentAccount.ID;
                        SecurityContext.Logout();
                    }
                    try
                    {
                        SecurityContext.AuthenticateMe(ownerId);

                        var _email = CoreContext.UserManager.GetUsers(ownerId).Email;
                        var calDavCal = new CalDavCalendar(caldavGuid, true);
                        var authorization = sharedPostfixIndex != -1 ? calDavCal.GetSystemAuthorization() : GetUserAuthorization(_email);



                        Logger.Info(String.Format("UpdateCalDavEvent eventURl: {0}", eventURl));

                        string ics = calDavCal.GetCollection(eventURl, authorization).Result.Data;
                        Logger.Info(String.Format("UpdateCalDavEvent: {0}", ics));
                        var existEvent = _dataProvider.GetEventIdByUid(eventGuid + "%", calendarId); // new function
                        var existCalendar = _dataProvider.GetCalendarById(calendarId);

                        var calendars = DDayICalParser.DeserializeCalendar(ics);
                        var _calendar = calendars == null ? null : calendars.FirstOrDefault();
                        var eventObj = _calendar == null || _calendar.Events == null ? null : _calendar.Events.FirstOrDefault();
                        if (eventObj != null && existCalendar.IsTodo == 0)
                        {
                            var name = eventObj.Summary;
                            var description = eventObj.Description ?? " ";

                            var alarm = eventObj.Alarms == null ? null : eventObj.Alarms.FirstOrDefault();
                            var alertType = EventAlertType.Default;
                            if (alarm != null)
                            {
                                if (alarm.Trigger.Duration != null)
                                {
                                    var alarmMinutes = alarm.Trigger.Duration.Value.Minutes;
                                    var alarmHours = alarm.Trigger.Duration.Value.Hours;
                                    var alarmDays = alarm.Trigger.Duration.Value.Days;
                                    switch (alarmMinutes)
                                    {
                                        case -5:
                                            alertType = EventAlertType.FiveMinutes;
                                            break;
                                        case -15:
                                            alertType = EventAlertType.FifteenMinutes;
                                            break;
                                        case -30:
                                            alertType = EventAlertType.HalfHour;
                                            break;
                                    }
                                    switch (alarmHours)
                                    {
                                        case -1:
                                            alertType = EventAlertType.Hour;
                                            break;
                                        case -2:
                                            alertType = EventAlertType.TwoHours;
                                            break;
                                    }
                                    if (alarmDays == -1)
                                        alertType = EventAlertType.Day;
                                }
                            }

                            //var utcStartDate = eventObj.IsAllDay ? eventObj.Start.Value : DDayICalParser.ToUtc(eventObj.Start);
                            //var utcEndDate = eventObj.IsAllDay ? eventObj.End.Value : DDayICalParser.ToUtc(eventObj.End);

                            //var rrule = RecurrenceRule.Parse(GetRRuleString(eventObj));
                            //var status = DDayICalParser.ConvertEventStatus(eventObj.Status);

                            if (existEvent != null)
                            {
                                var eventId = int.Parse(existEvent.Id);

                                var cal = new Ical.Net.Calendar();

                                var permissions = PublicItemCollection.GetForEvent(existEvent);
                                var sharingOptions = permissions.Items
                                    .Where(x => x.SharingOption.Id != AccessOption.OwnerOption.Id)
                                    .Select(x => new SharingParam
                                    {
                                        Id = x.Id,
                                        actionId = x.SharingOption.Id,
                                        isGroup = x.IsGroup
                                    }).ToList();

                                //eventObj.Start = new CalDateTime(DateTime.SpecifyKind(utcStartDate, DateTimeKind.Utc), TimeZoneInfo.Utc.Id);
                                //eventObj.End = new CalDateTime(DateTime.SpecifyKind(utcEndDate, DateTimeKind.Utc), TimeZoneInfo.Utc.Id);
                                //eventObj.Created = new CalDateTime(DateTime.SpecifyKind(eventObj.Created != null ? eventObj.Created.Value : DateTime.Now, DateTimeKind.Utc), TimeZoneInfo.Utc.Id);


                                //cal.Events.Add(eventObj);
                                //ics = DDayICalParser.SerializeCalendar(cal);
                                await UpdateEvent(eventId, calendarId.ToString(), ics, alertType,
                                            sharingOptions, true, ownerId.ToString()).ConfigureAwait(false);
                            }
                            else
                            {
                                await AddEvent(calendarId, ics, alertType, null, eventGuid).ConfigureAwait(false);
                            }
                        }
                        var todoObj = _calendar == null || _calendar.Todos == null ? null : _calendar.Todos.FirstOrDefault();
                        if (todoObj != null && existCalendar.IsTodo == 1)
                        {
                            var todoName = todoObj.Summary;
                            var todoDescription = todoObj.Description ?? " ";
                            var todoUtcStartDate = todoObj.Start != null ? DDayICalParser.ToUtc(todoObj.Start) : DateTime.MinValue;
                            var todoCompleted = todoObj.Completed != null ? DDayICalParser.ToUtc(todoObj.Completed) : DateTime.MinValue;

                            var existTodo = _dataProvider.GetTodoIdByUid(eventGuid + "%", calendarId);

                            if (existTodo != null)
                            {
                                var todoId = int.Parse(existTodo.Id);


                                UpdateTodo(
                                   calendarId,
                                   todoName,
                                   todoDescription,
                                   todoUtcStartDate,
                                   existTodo.Uid,
                                   todoCompleted);
                            }
                            else
                            {
                                CreateTodo(calendarId,
                                            todoName,
                                            todoDescription,
                                            todoUtcStartDate,
                                            eventGuid,
                                            todoCompleted);
                            }
                        }




                    }
                    finally
                    {
                        SecurityContext.Logout();
                        if (currentUserId != Guid.Empty)
                        {
                            SecurityContext.AuthenticateMe(currentUserId);
                        }
                    }
                }
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.ProtocolError && ex.Response != null)
                {
                    var resp = (HttpWebResponse)ex.Response;
                    if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Conflict)
                        Logger.Debug("ERROR: " + ex.Message);
                    else
                        Logger.Error("ERROR: " + ex.Message);
                }
                else
                {
                    Logger.Error("ERROR: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private DavRequest GetDavRequest(string email, string calendarId, Uri myUri, string calendarGuid, string eventUid)
        {
            var calDavServerUrl = myUri.Scheme + "://" + myUri.Host + "/caldav";
            var currentUserName = email.ToLower() + "@" + myUri.Host;
            var requestDeleteUrl = calDavServerUrl + "/" + HttpUtility.UrlEncode(currentUserName) + "/" + calendarGuid + "/" + eventUid + ".ics";
            var calDavCalendar = new CalDavCalendar(calendarId, false);
            var authorization = calDavCalendar.GetSystemAuthorization();
            var davRequest = new DavRequest()
            {
                Url = requestDeleteUrl,
                Authorization = authorization
            };

            return davRequest;
        }

        private async Task SharingEventTask(string old_ics, Uri myUri, BusinessObjects.Calendar calendarObj, Ical.Net.Calendar targetCalendar,
            TimeZoneInfo calendarObjTimeZone, List<SharingParam> sharingOptions, string[] uidData, string calendarId,
            DateTime createDate, List<SharingParam> calendarCharingList, BusinessObjects.Calendar cal, bool isShared, string eventUid, string oldCalendarGuid)
        {

            var currentUserEmail = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID).Email.ToLower();
            var calDavGuid = calendarObj != null ? calendarObj.calDavGuid : "";

            try
            {
                var updateCaldavEventTasks = new List<Task>();
                //event sharing ptions
                foreach (var sharingOption in sharingOptions)
                {
                    if (!sharingOption.IsGroup)
                    {
                        var user = CoreContext.UserManager.GetUsers(sharingOption.itemId);
                        if (CheckUserEmail(user))
                        {

                            if (oldCalendarGuid != "")
                            {
                                var davRequest = GetDavRequest(user.Email, calendarId, myUri, oldCalendarGuid, eventUid);
                                updateCaldavEventTasks.Add(RadicaleClient.RemoveAsync(davRequest));
                            }

                            updateCaldavEventTasks.Add(ReplaceSharingEvent(user, sharingOption.actionId, uidData[0], myUri, old_ics,
                                                calendarId, createDate, targetCalendar.TimeZones[0],
                                                calendarObj.TimeZone));
                        }
                    }
                    else
                    {
                        var users = CoreContext.UserManager.GetUsersByGroup(sharingOption.itemId);
                        foreach (var user in users)
                        {
                            if (CheckUserEmail(user))
                            {
                                if (oldCalendarGuid != "")
                                {
                                    var davRequest = GetDavRequest(user.Email, calendarId, myUri, oldCalendarGuid, eventUid);
                                    updateCaldavEventTasks.Add(RadicaleClient.RemoveAsync(davRequest));
                                }

                                updateCaldavEventTasks.Add(ReplaceSharingEvent(user, sharingOption.actionId, uidData[0], myUri, old_ics,
                                            calendarId, createDate, targetCalendar.TimeZones[0],
                                            calendarObjTimeZone));
                            }
                        }
                    }
                }

                //calendar sharing options
                foreach (var sharingOption in calendarCharingList)
                {
                    if (!sharingOption.IsGroup)
                    {
                        var user = CoreContext.UserManager.GetUsers(sharingOption.itemId);
                        if (CheckUserEmail(user))
                        {
                            if (oldCalendarGuid != "")
                            {
                                var davRequest = GetDavRequest(user.Email, calendarId, myUri, oldCalendarGuid, eventUid);
                                updateCaldavEventTasks.Add(RadicaleClient.RemoveAsync(davRequest));
                            }
                            updateCaldavEventTasks.Add(ReplaceSharingEvent(user, sharingOption.actionId, uidData[0], myUri, old_ics,
                                                calendarId, createDate, targetCalendar.TimeZones[0],
                                                calendarObj.TimeZone, cal.calDavGuid));
                        }
                    }
                    else
                    {
                        var users = CoreContext.UserManager.GetUsersByGroup(sharingOption.itemId);
                        foreach (var user in users)
                        {
                            if (CheckUserEmail(user))
                            {
                                if (oldCalendarGuid != "")
                                {
                                    var davRequest = GetDavRequest(user.Email, calendarId, myUri, oldCalendarGuid, eventUid);
                                    updateCaldavEventTasks.Add(RadicaleClient.RemoveAsync(davRequest));
                                }
                                updateCaldavEventTasks.Add(ReplaceSharingEvent(user, sharingOption.actionId, uidData[0], myUri, old_ics,
                                            calendarId, createDate, targetCalendar.TimeZones[0],
                                            calendarObjTimeZone, cal.calDavGuid));
                            }
                        }
                    }
                }
                if (!isShared)
                {
                    if (oldCalendarGuid != "")
                    {
                        var davRequest = GetDavRequest(currentUserEmail, calendarId, myUri, oldCalendarGuid, eventUid);
                        updateCaldavEventTasks.Add(RadicaleClient.RemoveAsync(davRequest));
                    }
                    updateCaldavEventTasks.Add(UpdateCaldavEventTask(old_ics, eventUid, true, calDavGuid, myUri, currentUserEmail,
                        createDate, targetCalendar.TimeZones[0], calendarObjTimeZone, false, isShared));
                }
                else
                {
                    var owner = CoreContext.UserManager.GetUsers(calendarObj.OwnerId);
                    if (CheckUserEmail(owner))
                    {
                        if (oldCalendarGuid != "")
                        {
                            var davRequest = GetDavRequest(owner.Email, calendarId, myUri, oldCalendarGuid, eventUid);
                            updateCaldavEventTasks.Add(RadicaleClient.RemoveAsync(davRequest));
                        }
                        updateCaldavEventTasks.Add(UpdateCaldavEventTask(old_ics, uidData[0], true, calendarObj.calDavGuid, myUri, owner.Email,
                            createDate, targetCalendar.TimeZones[0], calendarObjTimeZone));
                    }
                }

                await Task.WhenAll(updateCaldavEventTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
            }

        }


        private async Task UpdateCaldavTask(string old_ics, string currentEventUid, BusinessObjects.Calendar calendarObj, Uri myUri, Ical.Net.Calendar targetCalendar,
            TimeZoneInfo calendarObjTimeZone, List<SharingParam> sharingList, string[] split, List<SharingParam> sharingOptions)
        {

            var calDavGuid = calendarObj != null ? calendarObj.calDavGuid : "";
            var userId = SecurityContext.CurrentAccount.ID;
            var currentUserEmail = CoreContext.UserManager.GetUsers(userId).Email.ToLower();

            var updateCaldavEventTasks = new List<Task>();
            updateCaldavEventTasks.Add(UpdateCaldavEventTask(old_ics, currentEventUid, true, calDavGuid, myUri,
                                currentUserEmail, DateTime.Now,
                                targetCalendar.TimeZones[0], calendarObjTimeZone, false, userId != calendarObj.OwnerId));

            //calendar sharing list
            foreach (var sharingOption in sharingList)
            {
                var fullAccess = sharingOption.actionId == AccessOption.FullAccessOption.Id;

                if (!sharingOption.IsGroup)
                {
                    var user = CoreContext.UserManager.GetUsers(sharingOption.itemId);
                    if (CheckUserEmail(user))
                    {
                        var sharedEventUid = user.ID == calendarObj.OwnerId
                                                ? split[0]
                                                : fullAccess ? split[0] + "_write" : split[0];

                        updateCaldavEventTasks.Add(UpdateCaldavEventTask(old_ics, sharedEventUid, true, calDavGuid, myUri,
                                            user.Email, DateTime.Now, targetCalendar.TimeZones[0],
                                            calendarObjTimeZone, false, user.ID != calendarObj.OwnerId));
                    }
                }
                else
                {
                    var users = CoreContext.UserManager.GetUsersByGroup(sharingOption.itemId);

                    foreach (var user in users)
                    {
                        if (CheckUserEmail(user))
                        {
                            var sharedEventUid = user.ID == calendarObj.OwnerId
                                                ? split[0]
                                                : fullAccess ? split[0] + "_write" : split[0];
                            updateCaldavEventTasks.Add(UpdateCaldavEventTask(old_ics, sharedEventUid, true, calDavGuid, myUri,
                                            user.Email, DateTime.Now, targetCalendar.TimeZones[0],
                                            calendarObjTimeZone, false, user.ID != calendarObj.OwnerId));
                        }
                    }
                }
            }
            //event sharing list
            foreach (var sharingOption in sharingOptions)
            {
                var fullAccess = sharingOption.actionId == AccessOption.FullAccessOption.Id;

                if (!sharingOption.IsGroup)
                {
                    var user = CoreContext.UserManager.GetUsers(sharingOption.itemId);
                    if (CheckUserEmail(user))
                    {
                        var sharedEventUid = user.ID == calendarObj.OwnerId
                                                ? split[0]
                                                : fullAccess ? split[0] + "_write" : split[0];

                        updateCaldavEventTasks.Add(UpdateCaldavEventTask(old_ics, sharedEventUid, true, SharedEventsCalendar.CalendarId, myUri,
                                            user.Email, DateTime.Now, targetCalendar.TimeZones[0],
                                            calendarObjTimeZone, false, user.ID != calendarObj.OwnerId));
                    }

                }
                else
                {
                    var users = CoreContext.UserManager.GetUsersByGroup(sharingOption.itemId);

                    foreach (var user in users)
                    {
                        if (CheckUserEmail(user))
                        {
                            var sharedEventUid = user.ID == calendarObj.OwnerId
                                                ? split[0]
                                                : fullAccess ? split[0] + "_write" : split[0];

                            updateCaldavEventTasks.Add(UpdateCaldavEventTask(old_ics, sharedEventUid, true, SharedEventsCalendar.CalendarId, myUri,
                                            user.Email, DateTime.Now, targetCalendar.TimeZones[0],
                                            calendarObjTimeZone, false, user.ID != calendarObj.OwnerId));
                        }
                    }
                }
            }
            await Task.WhenAll(updateCaldavEventTasks).ConfigureAwait(false);
        }

        #endregion

        #region ICal/import

        /// <summary>
        /// Returns a link to the iCal related to the calendar with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Get iCal link
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <category>Calendars and subscriptions</category>
        /// <returns>iCal link</returns>
        [Read("{calendarId}/icalurl")]
        public string GetCalendariCalUrl(string calendarId)
        {
            var sig = Signature.Create(SecurityContext.CurrentAccount.ID, calendarId);
            var path = UrlPath.ResolveUrl(() => new CalendarApi().GetCalendariCalStream(calendarId, sig));
            return new Uri(_context.RequestContext.HttpContext.Request.GetUrlRewriter(), VirtualPathUtility.ToAbsolute("~/" + path)).ToString();
        }
        /// <summary>
        /// Returns a link to the CalDav related to the calendar with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Get CalDav link
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="uri" visible="false">Current URI</param>
        /// <category>Calendars and subscriptions</category>
        /// <returns>CalDav link</returns>
        [Read("{calendarId}/caldavurl")]
        public async Task<DavResponse> GetCalendarCalDavUrl(string calendarId, Uri uri = null)
        {
            var myUri = uri != null ? uri : HttpContext.Current.Request.GetUrlRewriter();

            var calDavServerUrl = myUri.Scheme + "://" + myUri.Host + "/caldav";
            var caldavHost = myUri.Host;
            var currentTenantId = CoreContext.TenantManager.GetCurrentTenant().TenantId;
            var userId = SecurityContext.CurrentAccount.ID;
            var user = CoreContext.UserManager.GetUsers(userId);
            if (!CheckUserEmail(user))
                return new DavResponse()
                {
                    Completed = false,
                    Error = "Invalid user email"
                };
            var userName = CoreContext.UserManager.GetUsers(userId).Email.ToLower();

            var curCaldavUserName = userName + "@" + caldavHost;
            using (_dataProvider = new DataProvider())
            {
                if (calendarId == "todo_calendar")
                {
                    var todoCalendars = _dataProvider.LoadTodoCalendarsForUser(SecurityContext.CurrentAccount.ID);
                    var userTimeZone = CoreContext.TenantManager.GetCurrentTenant().TimeZone;
                    var todoCal = new CalendarWrapper(new BusinessObjects.Calendar());

                    if (todoCalendars.Count == 0)
                    {
                        todoCal = CreateCalendar("Todo_calendar", "", BusinessObjects.Calendar.DefaultTextColor, BusinessObjects.Calendar.DefaultTodoBackgroundColor, userTimeZone.ToString(), EventAlertType.FifteenMinutes, null, null, 1).Result;

                        if (todoCal != null)
                        {
                            using (var db = DbManager.FromHttpContext("calendar"))
                            {
                                using (var tr = db.BeginTransaction())
                                {
                                    try
                                    {
                                        var dataCaldavGuid =
                                             db.ExecuteList(new SqlQuery("calendar_calendars")
                                               .Select("caldav_guid")
                                               .Where("id", todoCal.Id))
                                               .Select(r => r[0])
                                               .ToArray();
                                        var caldavGuid = dataCaldavGuid[0] != null
                                                 ? Guid.Parse(dataCaldavGuid[0].ToString())
                                                 : Guid.Empty;

                                        //var caldavCal = new CalDavCalendar(caldavGuid.ToString(), false);
                                        //var shara = caldavCal.GetRadicaleUrl(myUri.ToString(), userName, false, false, true, caldavGuid.ToString());
                                        var sharedCalUrl = new Uri(new Uri(calDavServerUrl), "/caldav/" + HttpUtility.UrlEncode(curCaldavUserName) + "/" + caldavGuid).ToString();


                                        var todoCalDavCreateResponse = new CalDavCalendar(caldavGuid.ToString(), false).GetCollection(sharedCalUrl, GetUserAuthorization(userName)).Result;
                                        if (!todoCalDavCreateResponse.Completed && todoCalDavCreateResponse.StatusCode == 404)
                                        {
                                            return await CreateCalDavCalendar(
                                                                    "Todo_calendar",
                                                                    "",
                                                                    BusinessObjects.Calendar.DefaultTodoBackgroundColor,
                                                                    caldavGuid.ToString(),
                                                                    myUri,
                                                                    curCaldavUserName,
                                                                    userName
                                                                ).ConfigureAwait(false);
                                        }
                                        todoCalDavCreateResponse.Data = sharedCalUrl;
                                        return todoCalDavCreateResponse;
                                    }
                                    catch (Exception exception)
                                    {
                                        Logger.Error("ERROR: " + exception.Message);
                                        return new DavResponse()
                                        {
                                            Completed = false,
                                            Error = exception.Message
                                        };
                                    }
                                }
                            }
                        }
                        else
                        {
                            return new DavResponse()
                            {
                                Completed = false,
                                Error = "Create calendar error"
                            };
                        }
                    }
                    else
                    {
                        var sharedCalUrl = new Uri(new Uri(calDavServerUrl), "/caldav/" + HttpUtility.UrlEncode(curCaldavUserName) + "/" + todoCalendars[0].calDavGuid).ToString();
                        var todoCalDavGetResponse = await new CalDavCalendar(todoCalendars[0].calDavGuid.ToString(), false).GetCollection(sharedCalUrl, GetUserAuthorization(userName));
                        if (!todoCalDavGetResponse.Completed && todoCalDavGetResponse.StatusCode == 404)
                        {
                            return await CreateCalDavCalendar(
                                                    "Todo_calendar",
                                                    "",
                                                    BusinessObjects.Calendar.DefaultTodoBackgroundColor,
                                                    todoCalendars[0].calDavGuid.ToString(),
                                                    myUri,
                                                    curCaldavUserName,
                                                    userName
                                                ).ConfigureAwait(false);
                        }
                        todoCalDavGetResponse.Data = sharedCalUrl;
                        return todoCalDavGetResponse;
                    }
                }

                if (calendarId == BirthdayReminderCalendar.CalendarId ||
                    calendarId == SharedEventsCalendar.CalendarId ||
                    calendarId == "crm_calendar" ||
                    calendarId.Contains("Project_"))
                {

                    if (SecurityContext.IsAuthenticated)
                    {
                        var sharedCalendar = GetCalendarById(calendarId);

                        var currentCaldavUserName = userName + "@" + caldavHost;
                        var sharedCalUrl = new Uri(new Uri(calDavServerUrl), "/caldav/" + currentCaldavUserName + "/" + calendarId + "-readonly").ToString();

                        var calendarResponse = await new CalDavCalendar(calendarId, false).GetCollection(sharedCalUrl, GetUserAuthorization(userName)).ConfigureAwait(false);
                        CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
                        if (!calendarResponse.Completed)
                        {
                            var sharedCalDavResponse = new DavResponse();
                            if (calendarResponse.StatusCode == 404)
                            {
                                sharedCalDavResponse = await CreateCalDavCalendar(
                                    sharedCalendar.UserCalendar.Name,
                                    sharedCalendar.UserCalendar.Description,
                                    sharedCalendar.TextColor,
                                    calendarId,
                                    myUri,
                                    currentCaldavUserName,
                                    userName,
                                    true
                                    );
                                CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
                            }
                            if (sharedCalDavResponse.Completed)
                            {
                                var calendarIcs = GetCalendariCalString(_dataProvider, calendarId, true);

                                var tenant = CoreContext.TenantManager.GetCurrentTenant();
                                await CreateCaldavSharedEvents(calendarId, calendarIcs, myUri, userName, sharedCalendar.UserCalendar, SecurityContext.CurrentAccount, tenant.TenantId).ConfigureAwait(false);
                                CoreContext.TenantManager.SetCurrentTenant(currentTenantId);


                                return sharedCalDavResponse;
                            }
                        }
                        else
                        {
                            calendarResponse.Data = sharedCalUrl;
                            return calendarResponse;
                        }
                    }
                    return new DavResponse()
                    {
                        Completed = false,
                        Error = "Authentication error"
                    };

                }

                var cal = _dataProvider.GetCalendarById(Convert.ToInt32(calendarId));
                var ownerId = cal.OwnerId;

                var isShared = ownerId != SecurityContext.CurrentAccount.ID;
                var calDavGuid = cal.calDavGuid;
                if (calDavGuid == "" || calDavGuid == Guid.Empty.ToString())
                {
                    calDavGuid = Guid.NewGuid().ToString();
                    _dataProvider.UpdateCalendarGuid(Convert.ToInt32(cal.Id), Guid.Parse(calDavGuid));
                }

                var calDavCalendar = new CalDavCalendar(calDavGuid.ToString(), isShared);
                var calUrl = calDavCalendar.GetRadicaleUrl(myUri.ToString(), userName, isShared, false, true, calDavGuid.ToString());

                Logger.Info("RADICALE REWRITE URL: " + myUri);

                var calDavResponse = calDavCalendar.GetCollection(calUrl, GetUserAuthorization(userName)).Result;

                if (!calDavResponse.Completed && calDavResponse.StatusCode == 404)
                {
                    return SyncCaldavCalendar(calendarId, cal.Name, cal.Description, cal.Context.HtmlBackgroundColor, Guid.Parse(calDavGuid), myUri, curCaldavUserName, userName, isShared, cal.SharingOptions).Result;
                }
                calDavResponse.Data = calUrl;
                return calDavResponse;
            }
                


        }

        private async Task<DavResponse> SyncCaldavCalendar(string calendarId,
                                            string name,
                                            string description,
                                            string backgroundColor,
                                            Guid calDavGuid,
                                            Uri myUri,
                                            string curCaldavUserName,
                                            string email,
                                            bool isShared = false,
                                            SharingOptions sharingOptions = null)
        {
            var createCalDavResponse = CreateCalDavCalendar(name, description, backgroundColor, calDavGuid.ToString(), myUri, curCaldavUserName, email, isShared).Result;

            BaseCalendar icalendar;
            int calId;

            var viewSettings = _dataProvider.GetUserViewSettings(SecurityContext.CurrentAccount.ID, new List<string> { calendarId });

            if (int.TryParse(calendarId, out calId))
            {
                icalendar = _dataProvider.GetCalendarById(calId);
                if (icalendar != null)
                {
                    icalendar = icalendar.GetUserCalendar(viewSettings.FirstOrDefault());
                }
            }
            else
            {
                //external
                icalendar = CalendarManager.Instance.GetCalendarForUser(SecurityContext.CurrentAccount.ID, calendarId);
                if (icalendar != null)
                {
                    icalendar = icalendar.GetUserCalendar(viewSettings.FirstOrDefault());
                }
            }

            if (icalendar == null)
                return new DavResponse()
                {
                    Completed = false,
                    Error = "Calendar not found"
                };

            var calendarIcs = GetCalendariCalString(_dataProvider, icalendar.Id, true);

            var tenant = CoreContext.TenantManager.GetCurrentTenant();
            if (isShared)
            {
                await CreateCaldavSharedEvents(calDavGuid.ToString(), calendarIcs, myUri, email, icalendar, SecurityContext.CurrentAccount, tenant.TenantId).ConfigureAwait(false);
            }
            else
            {
                await CreateCaldavEvents(calDavGuid.ToString(), myUri, email, icalendar, calendarIcs, tenant.TenantId).ConfigureAwait(false);
            }



            return createCalDavResponse;
        }





        /// <summary>
        /// Updates a calendar storage with a new one specified in the request.
        /// </summary>
        /// <short>
        /// Update a calendar storage
        /// </short>
        /// <param name="change">New calendar storage</param>
        /// <param name="key">Email key</param>
        /// <category>Calendars and subscriptions</category>
        /// <visible>false</visible>
        [Read("change_to_storage", false)] //NOTE: This method doesn't require auth!!!
        public async Task ChangeOfCalendarStorage(string change, string key)
        {
            var authInterval = TimeSpan.FromHours(1);
            var checkKeyResult = EmailValidationKeyProvider.ValidateEmailKey(change + ConfirmType.Auth, key, authInterval);
            if (checkKeyResult != EmailValidationKeyProvider.ValidationResult.Ok) throw new SecurityException("Access Denied.");

            var urlRewriter = HttpContext.Current.Request.GetUrlRewriter();
            var caldavUser = change.Split('/')[0];
            var portalName = caldavUser.Split('@')[2];

            if (change != null && portalName != null)
            {
                var calDavUrl = new Uri(urlRewriter.Scheme + "://" + portalName);

                await UpdateCalDavEvent(change, calDavUrl).ConfigureAwait(false);

            }
        }

        /// <summary>
        /// Deletes the specified information from the CalDav event.
        /// </summary>
        /// <short>
        /// Delete the CalDav event information
        /// </short>
        /// <param name="eventInfo">Event information that will be deleted</param>
        /// <param name="key">Email key</param>
        /// <category>Events</category>
        /// <visible>false</visible>
        [Read("caldav_delete_event", false)] //NOTE: This method doesn't require auth!!!
        public async Task CaldavDeleteEvent(string eventInfo, string key)
        {
            var authInterval = TimeSpan.FromHours(1);
            var checkKeyResult = EmailValidationKeyProvider.ValidateEmailKey(eventInfo + ConfirmType.Auth, key, authInterval);
            if (checkKeyResult != EmailValidationKeyProvider.ValidationResult.Ok) throw new SecurityException("Access Denied.");

            if (eventInfo != null)
            {
                var myUri = HttpContext.Current.Request.GetUrlRewriter();
                var calEvent = eventInfo.Split('/')[2].Replace("_write", "");
                var eventGuid = calEvent.Split('.')[0];

                var updateEventGuid = updatedEvents.Find((x) => x == eventGuid);
                if (updateEventGuid == null)
                {
                    await DeleteCalDavEvent(eventInfo, myUri).ConfigureAwait(false);
                }
                else
                {
                    updatedEvents.Remove(updateEventGuid);
                }
            }
        }

        private async Task DeleteCalDavEvent(string eventInfo, Uri myUri)
        {
            Thread.Sleep(1000);
            using (_dataProvider = new DataProvider())
            {
                var caldavGuid = eventInfo.Split('/')[1].Replace("-readonly", "");
                var calEvent = eventInfo.Split('/')[2].Replace("_write", ""); ;
                var eventGuid = calEvent.Split('.')[0];

                var currentUserId = Guid.Empty;
                if (SecurityContext.IsAuthenticated)
                {
                    currentUserId = SecurityContext.CurrentAccount.ID;
                    SecurityContext.Logout();
                }
                try
                {
                    if (caldavGuid != SharedEventsCalendar.CalendarId)
                    {
                        var calendar = _dataProvider.GetCalendarIdByCaldavGuid(caldavGuid);

                        var calendarId = Convert.ToInt32(calendar[0][0]);
                        var ownerId = Guid.Parse(calendar[0][1].ToString());


                        CoreContext.TenantManager.SetCurrentTenant(Convert.ToInt32(calendar[0][2]));
                        SecurityContext.CurrentUser = ownerId;

                        var existEvent = _dataProvider.GetEventIdByUid(eventGuid + "%", calendarId);
                        if (existEvent != null)
                        {
                            await RemoveEvent(Convert.ToInt32(existEvent.Id), null, EventRemoveType.AllSeries, myUri, true).ConfigureAwait(false);
                        }
                        else
                        {
                            var existTodo = _dataProvider.GetTodoByUid(eventGuid + "%");
                            if (existTodo != null)
                            {
                                await RemoveTodo(Convert.ToInt32(existTodo.Id), true).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        var existEvent = _dataProvider.GetEventIdOnlyByUid(eventGuid + "%");
                        if (existEvent != null)
                        {
                            CoreContext.TenantManager.SetCurrentTenant(existEvent.TenantId);
                            SecurityContext.CurrentUser = existEvent.OwnerId;

                            await RemoveEvent(Convert.ToInt32(existEvent.Id), null, EventRemoveType.AllSeries, myUri, true).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    SecurityContext.Logout();
                    if (currentUserId != Guid.Empty)
                    {
                        SecurityContext.CurrentUser = currentUserId;
                    }
                }
            }
        }


        /// <summary>
        /// Returns the iCal feed associated with the calendar by its ID and signagure specified in the request.
        /// </summary>
        /// <short>Get the iCal feed</short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="signature">Signature</param>
        /// <remarks>To get the feed you need to use the method returning the iCal feed link (it will generate the necessary signature).</remarks>
        /// <category>Calendars and subscriptions</category>
        /// <returns>Calendar iCal feed</returns>
        [Read("{calendarId}/ical/{signature}", false)] //NOTE: This method doesn't require auth!!!
        public iCalApiContentResponse GetCalendariCalStream(string calendarId, string signature)
        {
            try
            {
                //do not use compression
                var acceptEncoding = HttpContext.Current.Request.Headers["Accept-Encoding"];
                if (acceptEncoding != null)
                {
                    var encodings = acceptEncoding.Split(',');
                    if (encodings.Contains("gzip"))
                    {
                        encodings = (from x in encodings where x != "gzip" select x).ToArray();

                        Type t = HttpContext.Current.Request.Headers.GetType();
                        PropertyInfo propertyInfo = t.GetProperty("IsReadOnly", BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                        propertyInfo.SetValue(HttpContext.Current.Request.Headers, false, null);

                        HttpContext.Current.Request.Headers.Set("Accept-Encoding", string.Join(",", encodings));

                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }


            iCalApiContentResponse resp = null;
            var userId = Signature.Read<Guid>(signature, calendarId);
            if (CoreContext.UserManager.GetUsers(userId).ID != Core.Users.Constants.LostUser.ID)
            {
                var currentUserId = Guid.Empty;
                if (SecurityContext.IsAuthenticated)
                {
                    currentUserId = SecurityContext.CurrentAccount.ID;
                    SecurityContext.Logout();
                }
                try
                {
                    SecurityContext.CurrentUser = userId;
                    var icalFormat = GetCalendariCalString(_dataProvider, calendarId);
                    if (icalFormat != null)
                        resp = new iCalApiContentResponse(new MemoryStream(Encoding.UTF8.GetBytes(icalFormat)), calendarId + ".ics");
                }
                finally
                {
                    SecurityContext.Logout();
                    if (currentUserId != Guid.Empty)
                    {
                        SecurityContext.CurrentUser = currentUserId;
                    }
                }
            }
            return resp;
        }

        private string GetCalendariCalString(DataProvider dataProvider, string calendarId, bool ignoreCache = false)
        {
            Logger.Debug("GetCalendariCalString calendarId = " + calendarId);

            try
            {
                var result = ExportDataCache.Get(calendarId);

                if (!string.IsNullOrEmpty(result) && !ignoreCache)
                    return result;

                var stopWatch = new Stopwatch();
                stopWatch.Start();

                BaseCalendar icalendar;
                int calId;

                var viewSettings = dataProvider.GetUserViewSettings(SecurityContext.CurrentAccount.ID, new List<string> { calendarId });

                if (int.TryParse(calendarId, out calId))
                {
                    icalendar = dataProvider.GetCalendarById(calId);
                    if (icalendar != null)
                    {
                        icalendar = icalendar.GetUserCalendar(viewSettings.FirstOrDefault());
                    }
                }
                else
                {
                    //external                
                    icalendar = CalendarManager.Instance.GetCalendarForUser(SecurityContext.CurrentAccount.ID, calendarId);
                    if (icalendar != null)
                    {
                        icalendar = icalendar.GetUserCalendar(viewSettings.FirstOrDefault());
                    }
                }
                if (icalendar == null) return null;

                var ddayCalendar = DDayICalParser.ConvertCalendar(icalendar);
                ddayCalendar.Events.Clear();

                var events = icalendar.LoadEvents(SecurityContext.CurrentAccount.ID, DateTime.MinValue, DateTime.MaxValue);
                var eventIds = new List<int>();

                foreach (var e in events)
                {
                    int evtId;

                    if (int.TryParse(e.Id, out evtId))
                        eventIds.Add(evtId);
                }
                var eventsHystory = dataProvider.GetEventsHistory(eventIds.ToArray());

                foreach (var e in events)
                {
                    int evtId;
                    EventHistory evtHistory = null;

                    if (int.TryParse(e.Id, out evtId))
                        evtHistory = eventsHystory.FirstOrDefault(x => x.EventId == evtId);

                    var eventTz = e.TimeZone ?? icalendar.TimeZone;
                    var eventTzId = TimeZoneConverter.WindowsTzId2OlsonTzId(eventTz.Id);

                    if (evtHistory != null)
                    {
                        var mergedCalendar = evtHistory.GetMerged();
                        if (mergedCalendar == null || mergedCalendar.Events == null || !mergedCalendar.Events.Any())
                            continue;

                        var mergedEvent = mergedCalendar.Events.First();

                        mergedEvent.ExceptionDates = DDayICalParser.GetExceptionDates(e, eventTz, eventTzId);

                        if (!mergedEvent.IsAllDay && mergedEvent.DtStart.IsUtc && eventTz != TimeZoneInfo.Utc)
                        {
                            var _DtStart = mergedEvent.DtStart.Add(eventTz.GetUtcOffset(mergedEvent.DtStart.Value)).Value;
                            var _DtEnd = mergedEvent.DtEnd.Add(eventTz.GetUtcOffset(mergedEvent.DtEnd.Value)).Value;

                            mergedEvent.DtStart = new CalDateTime(_DtStart.Year, _DtStart.Month, _DtStart.Day, _DtStart.Hour, _DtStart.Minute, _DtStart.Second, eventTzId);
                            mergedEvent.DtEnd = new CalDateTime(_DtEnd.Year, _DtEnd.Month, _DtEnd.Day, _DtEnd.Hour, _DtEnd.Minute, _DtEnd.Second, eventTzId);

                        }
                        var alarm = mergedEvent.Alarms.FirstOrDefault();
                        if (alarm != null)
                        {
                            if (alarm.Trigger == null)
                            {
                                mergedEvent.Alarms.Clear();
                            }
                        }
                        else
                        {
                            mergedEvent.Alarms.Clear();
                        }
                        ddayCalendar.Events.Add(mergedEvent);
                    }
                    else
                    {
                        var convertedEvent = DDayICalParser.ConvertEvent(e as BaseEvent, eventTz);
                        if (convertedEvent == null) continue;

                        if (string.IsNullOrEmpty(convertedEvent.Uid))
                            convertedEvent.Uid = DataProvider.GetEventUid(e.Uid, e.Id);

                        var alarm = convertedEvent.Alarms.FirstOrDefault();

                        if (alarm != null)
                        {
                            if (alarm.Trigger == null)
                            {
                                convertedEvent.Alarms.Clear();
                            }
                        }
                        else
                        {
                            convertedEvent.Alarms.Clear();
                        }

                        ddayCalendar.Events.Add(convertedEvent);
                    }
                }
                ddayCalendar.TimeZones[0].Children.Clear();
                result = DDayICalParser.SerializeCalendar(ddayCalendar);

                //for yyyyMMdd/P1D date. Bug in the ical.net
                result = Regex.Replace(result, @"(\w*EXDATE;VALUE=DATE:\d{8})(/\w*)", "$1");

                ExportDataCache.Insert(calendarId, result);

                stopWatch.Stop();
                var timeSpan = stopWatch.Elapsed;
                var elapsedTime = String.Format("GetCalendariCalString elapsedTime = {0:00}:{1:00}:{2:00}.{3:00}",
                                                timeSpan.Hours,
                                                timeSpan.Minutes,
                                                timeSpan.Seconds,
                                                timeSpan.Milliseconds / 10);

                Logger.Debug(elapsedTime);

                return result;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return null;
            }
        }

        /// <summary>
        /// Imports the events from the iCal files specified in the request.
        /// </summary>
        /// <short>
        /// Import events from the iCal files
        /// </short>
        /// <param name="files">iCal formatted files with the events</param>
        /// <category>Events</category>
        /// <returns>The number of imported events</returns>
        [Create("import")]
        public int ImportEvents(IEnumerable<HttpPostedFileBase> files)
        {
            var calendar = LoadInternalCalendars().First(x => (!x.IsSubscription && x.IsTodo != 1));
            int calendarId;

            if (int.TryParse(calendar.Id, out calendarId))
                return ImportEvents(calendarId, files);

            throw new Exception(string.Format("Can't parse {0} to int", calendar.Id));
        }

        /// <summary>
        /// Imports the events from the iCal files to the calendar with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Import iCal events to the calendar
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="files">iCal formatted files with the events</param>
        /// <category>Events</category>
        /// <returns>The number of imported events</returns>
        [Create("{calendarId}/import")]
        public int ImportEvents(int calendarId, IEnumerable<HttpPostedFileBase> files)
        {
            var counter = 0;

            if (files != null)
            {
                foreach (var file in files)
                {
                    using (var reader = new StreamReader(file.InputStream))
                    {
                        var cals = DDayICalParser.DeserializeCalendar(reader);

                        counter = ImportEvents(calendarId, cals).Result;
                    }
                }
            }

            return counter;
        }

        /// <summary>
        /// Imports the events from the iCal files and attachments specified in the request.
        /// </summary>
        /// <short>
        /// Import events from the iCal files and attachments
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="files">iCal formatted files with the events and attachments</param>
        /// <visible>false</visible> 
        /// <category>Events</category>
        /// <returns>The number of imported events</returns>
        [Create("importFromAggregator")]
        public async Task<int> ImportEventsFromAggregator(int calendarId, IEnumerable<HttpPostedFileBase> files)
        {
            if (calendarId > 0)
            {
                if (files != null)
                {
                    var fileIcs = files.FirstOrDefault(x => x.FileName.Equals("calendar.ics"));
                    if (fileIcs != null)
                    {
                        var docs = files.Where(x => x != fileIcs);
                        using (var reader = new StreamReader(fileIcs.InputStream))
                        {
                            var cals = DDayICalParser.DeserializeCalendar(reader);
                            return await ImportEvents(calendarId, cals, docs).ConfigureAwait(false);
                        }
                    }
                }
            }

            var calendar = LoadInternalCalendars().First(x => (!x.IsSubscription && x.IsTodo != 1));
            if (int.TryParse(calendar.Id, out calendarId))
            {
                return await ImportEventsFromAggregator(calendarId, files).ConfigureAwait(false);
            }

            throw new Exception(string.Format("Can't parse {0} to int", calendar.Id));
        }

        /// <summary>
        /// Imports the events in the iCal format to the calendar with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Import ics
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="iCalString">iCal formatted string with the events to be imported</param>
        /// <category>Events</category>
        /// <returns>The number of imported events</returns>
        [Create("importIcs")]
        public int ImportEvents(int calendarId, string iCalString)
        {
            if (calendarId > 0)
            {
                var cals = DDayICalParser.DeserializeCalendar(iCalString);
                return ImportEvents(calendarId, cals).Result;
            }

            var calendar = LoadInternalCalendars().First(x => (!x.IsSubscription && x.IsTodo != 1));

            if (int.TryParse(calendar.Id, out calendarId))
                return ImportEvents(calendarId, iCalString);

            throw new Exception(string.Format("Can't parse {0} to int", calendar.Id));
        }


        private async Task<int> ImportEvents(int calendarId, IEnumerable<Ical.Net.Calendar> cals, IEnumerable<HttpPostedFileBase> docs = null)
        {
            var counter = 0;

            CheckPermissions(_dataProvider.GetCalendarById(calendarId), CalendarAccessRights.FullAccessAction);

            if (cals == null) return counter;

            var existCalendar = _dataProvider.GetCalendarById(calendarId);
            var existCalendarViewSettings = existCalendar.ViewSettings == null ? null : existCalendar.ViewSettings.FirstOrDefault();
            var existCalendarTimeZone = existCalendarViewSettings == null ? existCalendar.TimeZone : existCalendarViewSettings.TimeZone;

            var calendars = cals.Where(x => string.IsNullOrEmpty(x.Method) ||
                                            x.Method == Ical.Net.CalendarMethods.Publish ||
                                            x.Method == Ical.Net.CalendarMethods.Request ||
                                            x.Method == Ical.Net.CalendarMethods.Reply ||
                                            x.Method == Ical.Net.CalendarMethods.Cancel).ToList();

            var updateCaldavEventTask = new List<Task>();
            foreach (var calendar in calendars)
            {
                if (calendar.Events == null) continue;

                if (string.IsNullOrEmpty(calendar.Method))
                    calendar.Method = Ical.Net.CalendarMethods.Publish;

                foreach (var eventObj in calendar.Events)
                {
                    if (eventObj == null) continue;

                    var hasAttachments = eventObj.Attachments != null && eventObj.Attachments.Any();

                    if (hasAttachments)
                    {
                        if (docs != null && docs.Count() > 0)
                        {
                            SaveAttachmentsAndChangeUri(docs, eventObj.Attachments);
                        }
                        else if (calendar.Method.Equals(Ical.Net.CalendarMethods.Cancel))
                        {
                            eventObj.Attachments.Clear();
                            hasAttachments = false;
                        }
                    }

                    var tmpCalendar = calendar.Copy<Ical.Net.Calendar>();
                    tmpCalendar.Events.Clear();
                    tmpCalendar.Events.Add(eventObj);

                    string rrule;
                    var ics = DDayICalParser.SerializeCalendar(tmpCalendar);

                    var eventHistory = _dataProvider.GetEventHistory(eventObj.Uid);

                    if (eventHistory == null)
                    {
                        rrule = GetRRuleString(eventObj);

                        var eventTimeZone = string.IsNullOrEmpty(eventObj.Start.TzId) ? existCalendarTimeZone : TimeZoneConverter.GetTimeZone(eventObj.Start.TzId);

                        if (!string.IsNullOrEmpty(rrule) && eventObj.Start.IsUtc)
                        {
                            eventTimeZone = existCalendarTimeZone;
                        }

                        var utcStartDate = eventObj.IsAllDay ? eventObj.Start.Value : DDayICalParser.ToUtc(eventObj.Start);
                        var utcEndDate = eventObj.IsAllDay ? eventObj.End.Value : DDayICalParser.ToUtc(eventObj.End);

                        if (eventObj.IsAllDay && utcStartDate.Date < utcEndDate.Date)
                            utcEndDate = utcEndDate.AddDays(-1);

                        try
                        {
                            var uid = eventObj.Uid;
                            string[] split = uid.Split(new Char[] { '@' });

                            var calDavGuid = existCalendar != null ? existCalendar.calDavGuid : "";
                            var myUri = HttpContext.Current.Request.GetUrlRewriter();
                            var currentUserEmail = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID).Email.ToLower();

                            var currentTenantId = TenantProvider.CurrentTenantID;
                            try
                            {
                                CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
                                updateCaldavEventTask.Add(UpdateCaldavEventTask(ics, split[0], true, calDavGuid, myUri, currentUserEmail, DateTime.Now, tmpCalendar.TimeZones[0], existCalendarTimeZone));
                            }
                            catch (Exception ex)
                            {
                                LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
                            }


                        }
                        catch (Exception e)
                        {
                            Logger.Error(e.Message);
                        }

                        //updateEvent(ics, split[0], calendarId.ToString(), true, DateTime.Now, tmpCalendar.TimeZones[0], existCalendar.TimeZone);

                        var result = CreateEvent(calendarId,
                                                 eventObj.Summary,
                                                 eventObj.Description,
                                                 utcStartDate,
                                                 utcEndDate,
                                                 RecurrenceRule.Parse(rrule),
                                                 EventAlertType.Default,
                                                 eventObj.IsAllDay,
                                                 null,
                                                 eventObj.Uid,
                                                 calendar.Method == Ical.Net.CalendarMethods.Cancel ? EventStatus.Cancelled : DDayICalParser.ConvertEventStatus(eventObj.Status),
                                                 eventObj.Created != null ? eventObj.Created.Value : DateTime.Now,
                                                 eventTimeZone,
                                                 hasAttachments);

                        var eventId = result != null && result.Any() ? Int32.Parse(result.First().Id) : 0;

                        if (eventId > 0)
                        {
                            _dataProvider.AddEventHistory(calendarId, eventObj.Uid, eventId, ics);

                            if (hasAttachments)
                            {
                                SaveAttachments(eventObj.Attachments, eventId.ToString());
                            }

                            counter++;
                        }
                    }
                    else
                    {
                        if (eventHistory.Contains(tmpCalendar)) continue;

                        eventHistory = _dataProvider.AddEventHistory(eventHistory.CalendarId, eventHistory.EventUid,
                                                                     eventHistory.EventId, ics);

                        var mergedCalendar = eventHistory.GetMerged();

                        if (mergedCalendar == null || mergedCalendar.Events == null || !mergedCalendar.Events.Any()) continue;

                        var mergedEvent = mergedCalendar.Events.First();

                        rrule = GetRRuleString(mergedEvent);

                        var eventTimeZone = string.IsNullOrEmpty(eventObj.Start.TzId) ? existCalendarTimeZone : TimeZoneConverter.GetTimeZone(eventObj.Start.TzId);

                        if (!string.IsNullOrEmpty(rrule) && eventObj.Start.IsUtc)
                        {
                            eventTimeZone = existCalendarTimeZone;
                        }

                        var utcStartDate = mergedEvent.IsAllDay ? mergedEvent.Start.Value : DDayICalParser.ToUtc(mergedEvent.Start);
                        var utcEndDate = mergedEvent.IsAllDay ? mergedEvent.End.Value : DDayICalParser.ToUtc(mergedEvent.End);

                        if (mergedEvent.IsAllDay && utcStartDate.Date < utcEndDate.Date)
                            utcEndDate = utcEndDate.AddDays(-1);

                        var targetEvent = _dataProvider.GetEventById(eventHistory.EventId);
                        var permissions = PublicItemCollection.GetForEvent(targetEvent);
                        var sharingOptions = permissions.Items
                            .Where(x => x.SharingOption.Id != AccessOption.OwnerOption.Id)
                            .Select(x => new SharingParam
                            {
                                Id = x.Id,
                                actionId = x.SharingOption.Id,
                                isGroup = x.IsGroup
                            }).ToList();

                        try
                        {
                            var uid = eventObj.Uid;
                            string[] split = uid.Split(new Char[] { '@' });

                            var calDavGuid = existCalendar != null ? existCalendar.calDavGuid : "";
                            var myUri = HttpContext.Current.Request.GetUrlRewriter();
                            var currentUser = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID);
                            var currentUserEmail = CheckUserEmail(currentUser) ? currentUser.Email.ToLower() : null;

                            if (currentUserEmail != null)
                            {
                                var currentTenantId = TenantProvider.CurrentTenantID;

                                try
                                {
                                    CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
                                    updateCaldavEventTask.Add(UpdateCaldavEventTask(ics, split[0], true, calDavGuid, myUri, currentUserEmail, DateTime.Now, tmpCalendar.TimeZones[0], existCalendarTimeZone));
                                }
                                catch (Exception ex)
                                {
                                    LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Error(e.Message);
                        }

                        //updateEvent(ics, split[0], calendarId.ToString(), true, DateTime.Now, tmpCalendar.TimeZones[0], existCalendar.TimeZone);

                        var result = CreateEvent(eventHistory.CalendarId,
                                        mergedEvent.Summary,
                                        mergedEvent.Description,
                                        utcStartDate,
                                        utcEndDate,
                                        RecurrenceRule.Parse(rrule),
                                        EventAlertType.Default,
                                        mergedEvent.IsAllDay,
                                        sharingOptions,
                                        mergedEvent.Uid,
                                        DDayICalParser.ConvertEventStatus(mergedEvent.Status),
                                        eventObj.Created != null ? eventObj.Created.Value : DateTime.Now,
                                        eventTimeZone,
                                        hasAttachments);

                        var eventId = result != null && result.Any() ? Int32.Parse(result.First().Id) : 0;

                        if (eventId > 0)
                        {
                            if (hasAttachments)
                            {
                                SaveAttachments(eventObj.Attachments, eventId.ToString());
                            }

                            counter++;
                        }
                    }
                }
            }

            await Task.WhenAll(updateCaldavEventTask).ConfigureAwait(false);

            return counter;
        }

        /// <summary>
        /// Creates a calendar by the link to the external iCal feed.
        /// </summary>
        /// <short>
        /// Create a calendar by iCal URL
        /// </short>
        /// <param name="iCalUrl">Link to the external iCal feed</param>
        /// <param name="name">Calendar name</param>
        /// <param name="textColor">Event text color</param>
        /// <param name="backgroundColor">Event background name</param>
        /// <category>Calendars and subscriptions</category>
        /// <returns>Created calendar</returns>
        [Create("calendarUrl")]
        public CalendarWrapper CreateCalendarStream(string iCalUrl, string name, string textColor, string backgroundColor)
        {
            var cal = iCalendar.GetFromUrl(iCalUrl);
            if (cal.isEmptyName)
                cal.Name = iCalUrl;

            if (String.IsNullOrEmpty(name))
                name = cal.Name;

            textColor = (textColor ?? "").Trim();
            backgroundColor = (backgroundColor ?? "").Trim();

            var calendar = _dataProvider.CreateCalendar(
                        SecurityContext.CurrentAccount.ID, name, cal.Description ?? "", textColor, backgroundColor,
                        cal.TimeZone, cal.EventAlertType, iCalUrl, null, new List<UserViewSettings>(), Guid.Empty);

            if (calendar != null)
            {
                var calendarWrapperr = UpdateCalendarView(calendar.Id, calendar.Name, textColor, backgroundColor, calendar.TimeZone.Id, cal.EventAlertType, false);
                return calendarWrapperr;
            }

            return null;
        }

        #endregion

        #region Events

        /// <summary>
        /// Creates a new event in the default calendar with the parameters specified in the request.
        /// </summary>
        /// <short>
        /// Create a new event in the default calendar
        /// </short>
        /// <param name="name">Event name</param>
        /// <param name="description">Event description</param>
        /// <param name="startDate">Event start date</param>
        /// <param name="endDate">Event end date</param>
        /// <param name="repeatType">Event recurrence type (RRULE string in the iCal format)</param>
        /// <param name="alertType">Event notification type</param>
        /// <param name="isAllDayLong">Event duration type: all day long or not</param>
        /// <param name="sharingOptions">Event sharing access parameters</param>
        /// <category>Events</category>
        /// <returns>List of events</returns>
        [Create("event")]
        public List<EventWrapper> AddEvent(string name, string description, ApiDateTime startDate, ApiDateTime endDate, string repeatType, EventAlertType alertType, bool isAllDayLong, List<SharingParam> sharingOptions)
        {
            var calendar = LoadInternalCalendars().First(x => (!x.IsSubscription && x.IsTodo != 1));
            int calendarId;

            if (int.TryParse(calendar.Id, out calendarId))
            {
                var cal = new Ical.Net.Calendar();
                cal.Events.Add(DDayICalParser.CreateEvent(name, description, startDate.UtcTime, endDate.UtcTime, repeatType, isAllDayLong, EventStatus.Confirmed));
                return AddEvent(calendarId, DDayICalParser.SerializeCalendar(cal), alertType, sharingOptions).Result;
            }

            throw new Exception(string.Format("Can't parse {0} to int", calendar.Id));
        }

        /// <summary>
        /// Creates a new event in the selected calendar with the parameters specified in the request.
        /// </summary>
        /// <short>
        /// Create a new event in the selected calendar
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="name">Event name</param>
        /// <param name="description">Event description</param>
        /// <param name="startDate">Event start date</param>
        /// <param name="endDate">Event end date</param>
        /// <param name="repeatType">Event recurrence type (RRULE string in iCal format)</param>
        /// <param name="alertType">Event notification type</param>
        /// <param name="isAllDayLong">Event duration type: all day long or not</param>
        /// <param name="sharingOptions">Event sharing access parameters</param>
        /// <category>Events</category>
        /// <returns>List of events</returns>
        [Create("{calendarId}/event")]
        public List<EventWrapper> AddEvent(int calendarId, string name, string description, ApiDateTime startDate, ApiDateTime endDate, string repeatType, EventAlertType alertType, bool isAllDayLong, List<SharingParam> sharingOptions)
        {
            var cal = new Ical.Net.Calendar();
            cal.Events.Add(DDayICalParser.CreateEvent(name, description, startDate.UtcTime, endDate.UtcTime, repeatType, isAllDayLong, EventStatus.Confirmed));
            return AddEvent(calendarId, DDayICalParser.SerializeCalendar(cal), alertType, sharingOptions).Result;
        }

        private List<EventWrapper> CreateEvent(int calendarId, string name, string description, DateTime utcStartDate, DateTime utcEndDate, RecurrenceRule rrule, EventAlertType alertType, bool isAllDayLong, List<SharingParam> sharingOptions, string uid, EventStatus status, DateTime createDate, TimeZoneInfo eventTimeZone, bool hasAttachments)
        {
            var sharingOptionsList = sharingOptions ?? new List<SharingParam>();

            name = (name ?? "").Trim();
            description = (description ?? "").Trim();

            if (!string.IsNullOrEmpty(uid))
            {
                var existEvent = _dataProvider.GetEventByUid(uid);

                if (existEvent != null)
                {
                    return UpdateEvent(existEvent.CalendarId,
                                       int.Parse(existEvent.Id),
                                       name,
                                       description,
                                       new ApiDateTime(utcStartDate, TimeZoneInfo.Utc),
                                       new ApiDateTime(utcEndDate, TimeZoneInfo.Utc),
                                       rrule.ToString(),
                                       alertType,
                                       isAllDayLong,
                                       sharingOptions,
                                       status,
                                       createDate,
                                       hasAttachments,
                                       false,
                                       "",
                                       eventTimeZone);
                }
            }

            var cal = _dataProvider.GetCalendarById(calendarId);

            CheckPermissions(cal, CalendarAccessRights.FullAccessAction);

            var evt = _dataProvider.CreateEvent(calendarId,
                                                SecurityContext.CurrentAccount.ID,
                                                name,
                                                description,
                                                utcStartDate,
                                                utcEndDate,
                                                rrule,
                                                alertType,
                                                isAllDayLong,
                                                sharingOptionsList.Select(o => o as SharingOptions.PublicItem).ToList(),
                                                uid,
                                                status,
                                                createDate,
                                                eventTimeZone,
                                                hasAttachments);

            if (evt != null)
            {
                foreach (var opt in sharingOptionsList)
                    if (String.Equals(opt.actionId, AccessOption.FullAccessOption.Id, StringComparison.InvariantCultureIgnoreCase))
                        CoreContext.AuthorizationManager.AddAce(new AzRecord(opt.Id, CalendarAccessRights.FullAccessAction.ID, Common.Security.Authorizing.AceType.Allow, evt));

                //notify
                CalendarNotifyClient.NotifyAboutSharingEvent(evt);

                return new EventWrapper(evt, SecurityContext.CurrentAccount.ID,
                                        _dataProvider.GetTimeZoneForCalendar(SecurityContext.CurrentAccount.ID, calendarId))
                                        .GetList(utcStartDate, utcStartDate.AddMonths(_monthCount));
            }
            return null;
        }

        /// <summary>
        /// Updates the existing event in the selected calendar with the parameters specified in the request.
        /// </summary>
        /// <short>
        /// Update an event
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="eventId">Event ID</param>
        /// <param name="name">New event name</param>
        /// <param name="description">New event description</param>
        /// <param name="startDate">New event start date</param>
        /// <param name="endDate">New event end date</param>
        /// <param name="repeatType">New event recurrence type (RRULE string in iCal format)</param>
        /// <param name="alertType">New event notification type</param>
        /// <param name="isAllDayLong">New event duration type: all day long or not</param>
        /// <param name="sharingOptions">New event sharing access parameters</param>
        /// <param name="status">New event status</param>
        /// <category>Events</category>
        /// <returns>Updated list of events</returns>
        [Update("{calendarId}/{eventId}")]
        public List<EventWrapper> Update(string calendarId, int eventId, string name, string description, ApiDateTime startDate, ApiDateTime endDate, string repeatType, EventAlertType alertType, bool isAllDayLong, List<SharingParam> sharingOptions, EventStatus status)
        {
            var cal = new Ical.Net.Calendar();
            cal.Events.Add(DDayICalParser.CreateEvent(name, description, startDate.UtcTime, endDate.UtcTime, repeatType, isAllDayLong, status));
            return UpdateEvent(eventId, calendarId, DDayICalParser.SerializeCalendar(cal), alertType, sharingOptions).Result;
        }


        private List<EventWrapper> UpdateEvent(string calendarId, int eventId, string name, string description, ApiDateTime startDate, ApiDateTime endDate, string repeatType, EventAlertType alertType, bool isAllDayLong, List<SharingParam> sharingOptions, EventStatus status, DateTime createDate, bool hasAttachments, bool fromCalDavServer = false, string ownerId = "", TimeZoneInfo eventTimeZone = null)
        {
            var sharingOptionsList = sharingOptions ?? new List<SharingParam>();

            var oldEvent = _dataProvider.GetEventById(eventId);
            var ownerGuid = fromCalDavServer ? Guid.Parse(ownerId) : Guid.Empty; //get userGuid in the case of a request from the server
            if (oldEvent == null)
                throw new Exception(Resources.CalendarApiResource.ErrorItemNotFound);

            var cal = _dataProvider.GetCalendarById(Int32.Parse(oldEvent.CalendarId));

            if (!fromCalDavServer)
            {
                if (!oldEvent.OwnerId.Equals(SecurityContext.CurrentAccount.ID) &&
                    !CheckPermissions(oldEvent, CalendarAccessRights.FullAccessAction, true) &&
                    !CheckPermissions(cal, CalendarAccessRights.FullAccessAction, true))
                    throw new System.Security.SecurityException(Resources.CalendarApiResource.ErrorAccessDenied);

            }
            name = (name ?? "").Trim();
            description = (description ?? "").Trim();

            TimeZoneInfo timeZone;

            var calId = int.Parse(oldEvent.CalendarId);

            if (!int.TryParse(calendarId, out calId))
            {
                calId = int.Parse(oldEvent.CalendarId);
                timeZone = fromCalDavServer ? _dataProvider.GetTimeZoneForSharedEventsCalendar(ownerGuid) : _dataProvider.GetTimeZoneForSharedEventsCalendar(SecurityContext.CurrentAccount.ID);
            }
            else
                timeZone = fromCalDavServer ? _dataProvider.GetTimeZoneForCalendar(ownerGuid, calId) : _dataProvider.GetTimeZoneForCalendar(SecurityContext.CurrentAccount.ID, calId);

            var rrule = RecurrenceRule.Parse(repeatType);
            var evt = _dataProvider.UpdateEvent(eventId, calId,
                                                oldEvent.OwnerId, name, description, startDate.UtcTime, endDate.UtcTime, rrule, alertType, isAllDayLong,
                                                sharingOptionsList.Select(o => o as SharingOptions.PublicItem).ToList(), status, createDate, eventTimeZone, hasAttachments);

            if (evt != null)
            {
                //clear old rights
                CoreContext.TenantManager.SetCurrentTenant(TenantProvider.CurrentTenantID);
                CoreContext.AuthorizationManager.RemoveAllAces(evt);

                foreach (var opt in sharingOptionsList)
                    if (String.Equals(opt.actionId, AccessOption.FullAccessOption.Id, StringComparison.InvariantCultureIgnoreCase))
                        CoreContext.AuthorizationManager.AddAce(new AzRecord(opt.Id, CalendarAccessRights.FullAccessAction.ID, Common.Security.Authorizing.AceType.Allow, evt));

                //notify
                CalendarNotifyClient.NotifyAboutSharingEvent(evt, oldEvent);

                evt.CalendarId = calendarId;
                return fromCalDavServer ? new EventWrapper(evt, ownerGuid, timeZone).GetList(startDate.UtcTime, startDate.UtcTime.AddMonths(_monthCount)) : new EventWrapper(evt, SecurityContext.CurrentAccount.ID, timeZone).GetList(startDate.UtcTime, startDate.UtcTime.AddMonths(_monthCount));
            }
            return null;
        }



        /// <summary>
        /// Creates a new task in the selected calendar with the parameters specified in the request.
        /// </summary>
        /// <short>
        /// Create a new task
        /// </short>
        /// <param name="ics">Task in the iCal format</param>
        /// <param name="todoUid">Task UID</param>
        /// <category>Tasks</category>
        /// <returns>Task</returns>
        [Create("icstodo")]
        public async Task<List<TodoWrapper>> AddTodo(string ics, string todoUid = null)
        {

            var old_ics = ics;

            var todoCalendars = _dataProvider.LoadTodoCalendarsForUser(SecurityContext.CurrentAccount.ID);
            var userTimeZone = CoreContext.TenantManager.GetCurrentTenant().TimeZone;

            var todoCal = new CalendarWrapper(new BusinessObjects.Calendar());

            if (todoCalendars.Count == 0)
            {
                todoCal = CreateCalendar("Todo_calendar", "", BusinessObjects.Calendar.DefaultTextColor, BusinessObjects.Calendar.DefaultTodoBackgroundColor, userTimeZone.ToString(), EventAlertType.FifteenMinutes, null, null, 1).Result;
            }

            var calendarId = Convert.ToInt32(todoCalendars.Count == 0 ? todoCal.Id : todoCalendars.FirstOrDefault().Id);

            if (calendarId <= 0)
            {
                var defaultCalendar = LoadInternalCalendars().First(x => (!x.IsSubscription && x.IsTodo != 1));
                if (!int.TryParse(defaultCalendar.Id, out calendarId))
                    throw new Exception(string.Format("Can't parse {0} to int", defaultCalendar.Id));
            }
            var calendars = DDayICalParser.DeserializeCalendar(ics);

            if (calendars == null) return null;

            var calendar = calendars.FirstOrDefault();

            if (calendar == null || calendar.Todos == null) return null;

            var todoObj = calendar.Todos.FirstOrDefault();

            if (todoObj == null) return null;

            var calendarObj = todoCalendars.Count == 0 ? _dataProvider.GetCalendarById(Convert.ToInt32(todoCal.Id)) : todoCalendars.FirstOrDefault();
            var calendarObjViewSettings = calendarObj != null && calendarObj.ViewSettings != null ? calendarObj.ViewSettings.FirstOrDefault() : null;

            var targetCalendar = DDayICalParser.ConvertCalendar(calendarObj != null ? calendarObj.GetUserCalendar(calendarObjViewSettings) : null);

            if (targetCalendar == null) return null;

            var utcStartDate = todoObj.Start != null ? DDayICalParser.ToUtc(todoObj.Start) : DateTime.MinValue;

            todoUid = todoUid == null ? null : string.Format("{0}@{1}", todoUid, DataProvider.EventUidDomain);


            var result = CreateTodo(calendarId,
                                    todoObj.Summary,
                                    todoObj.Description,
                                    utcStartDate,
                                    DataProvider.GetEventUid(todoUid),
                                    DateTime.MinValue);

            if (result == null || !result.Any()) return null;

            var todo = result.First();

            todoObj.Uid = todo.Uid;

            targetCalendar.Method = Ical.Net.CalendarMethods.Request;
            targetCalendar.Todos.Clear();
            targetCalendar.Todos.Add(todoObj);

            try
            {
                var uid = todo.Uid;
                string[] split = uid.Split(new Char[] { '@' });

                var calDavGuid = calendarObj != null ? calendarObj.calDavGuid : "";
                var myUri = HttpContext.Current.Request.GetUrlRewriter();
                var currentUser = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID);
                var currentUserEmail = CheckUserEmail(currentUser) ? currentUser.Email.ToLower() : null;

                if (currentUserEmail != null)
                {
                    var currentTenantId = TenantProvider.CurrentTenantID;
                    try
                    {
                        await UpdateCaldavEventTask(old_ics, split[0], true, calDavGuid, myUri, currentUserEmail).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error(e.Message);
            }


            return result;

        }
        /// <summary>
        /// Updates the existing task with the parameters specified in the request.
        /// </summary>
        /// <short>
        /// Update a task
        /// </short>
        /// <param name="todoId">Task ID</param>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="ics">Task in the iCal format</param>
        /// <param name="fromCalDavServer">Defines if the request is from the CalDav server or not</param>
        /// <category>Tasks</category>
        /// <returns>Updated task</returns>
        [Update("icstodo")]
        public async Task<List<TodoWrapper>> UpdateTodo(string calendarId, string ics, string todoId, bool fromCalDavServer = false)
        {
            var todo = _dataProvider.GetTodoById(Convert.ToInt32(todoId));
            if (todo == null)
                throw new Exception(Resources.CalendarApiResource.ErrorItemNotFound);
            var old_ics = ics;

            var cal = _dataProvider.GetCalendarById(Int32.Parse(todo.CalendarId));
            if (!fromCalDavServer)
            {
                if (!todo.OwnerId.Equals(SecurityContext.CurrentAccount.ID) &&
                    !CheckPermissions(todo, CalendarAccessRights.FullAccessAction, true) &&
                    !CheckPermissions(cal, CalendarAccessRights.FullAccessAction, true))
                    throw new System.Security.SecurityException(Resources.CalendarApiResource.ErrorAccessDenied);
            }
            int calId;

            if (!int.TryParse(calendarId, out calId))
            {
                calId = int.Parse(todo.CalendarId);
            }

            var calendars = DDayICalParser.DeserializeCalendar(ics);

            if (calendars == null) return null;

            var calendar = calendars.FirstOrDefault();

            if (calendar == null || calendar.Events == null) return null;

            var todoObj = calendar.Todos.FirstOrDefault();

            if (todoObj == null) return null;

            var calendarObj = _dataProvider.GetCalendarById(calId);
            var calendarObjViewSettings = calendarObj != null && calendarObj.ViewSettings != null ? calendarObj.ViewSettings.FirstOrDefault() : null;
            var targetCalendar = DDayICalParser.ConvertCalendar(calendarObj != null ? calendarObj.GetUserCalendar(calendarObjViewSettings) : null);


            if (targetCalendar == null) return null;


            todoObj.Uid = todo.Uid;



            var completed = todoObj.Completed == null ? DateTime.MinValue : DDayICalParser.ToUtc(todoObj.Completed);
            var utcStartDate = todoObj.DtStart != null ? DDayICalParser.ToUtc(todoObj.DtStart) : DateTime.MinValue;

            var result = UpdateTodo(
                                   int.Parse(calendarId),
                                   todoObj.Summary,
                                   todoObj.Description,
                                   utcStartDate,
                                   todoObj.Uid,
                                   completed);

            if (!fromCalDavServer)
            {
                try
                {
                    var uid = todo.Uid;
                    string[] split = uid.Split(new Char[] { '@' });

                    var calDavGuid = calendarObj != null ? calendarObj.calDavGuid : "";
                    var myUri = HttpContext.Current.Request.GetUrlRewriter();

                    var currentUser = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID);
                    var currentUserEmail = CheckUserEmail(currentUser) ? currentUser.Email.ToLower() : null;

                    if (currentUserEmail != null)
                    {
                        var currentTenantId = TenantProvider.CurrentTenantID;
                        try
                        {
                            await UpdateCaldavEventTask(old_ics, split[0], true, calDavGuid, myUri, currentUserEmail).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e.Message);
                }

            }


            return result;

        }

        /// <summary>
        /// Deletes a task with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Delete a task
        /// </short>
        /// <param name="todoId">Task ID</param>
        /// <param name="fromCaldavServer">Defines if the request is from the CalDav server or not</param>
        /// <category>Tasks</category>
        [Delete("todos/{todoId}")]
        public async Task RemoveTodo(int todoId, bool fromCaldavServer = false)
        {
            var todo = _dataProvider.GetTodoById(todoId);

            var uid = todo.Uid;
            string[] split = uid.Split(new Char[] { '@' });

            _dataProvider.RemoveTodo(todoId);

            if (!fromCaldavServer)
            {
                var email = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID).Email;
                var myUri = HttpContext.Current.Request.GetUrlRewriter();
                await deleteEvent(split[0], todo.CalendarId, email, myUri).ConfigureAwait(false);
            }

        }
        private List<TodoWrapper> UpdateTodo(int calendarId, string name, string description, DateTime utcStartDate, string uid, DateTime completed)
        {
            name = (name ?? "").Trim();
            description = (description ?? "").Trim();

            if (!string.IsNullOrEmpty(uid))
            {
                var existTodo = _dataProvider.GetTodoByUid(uid);
                CheckPermissions(_dataProvider.GetCalendarById(calendarId), CalendarAccessRights.FullAccessAction);

                var todo = _dataProvider.UpdateTodo(existTodo.Id, calendarId, SecurityContext.CurrentAccount.ID, name, description, utcStartDate, uid, completed);

                if (todo != null)
                {

                    var todoResult = new TodoWrapper(todo, SecurityContext.CurrentAccount.ID,
                                            _dataProvider.GetTimeZoneForCalendar(SecurityContext.CurrentAccount.ID, calendarId))
                                            .GetList();
                    return todoResult;
                }
            }
            return null;
        }
        private List<TodoWrapper> CreateTodo(int calendarId, string name, string description, DateTime utcStartDate, string uid, DateTime completed)
        {
            name = (name ?? "").Trim();
            description = (description ?? "").Trim();

            if (!string.IsNullOrEmpty(uid))
            {
                var existTodo = _dataProvider.GetTodoByUid(uid);

                if (existTodo != null)
                {
                    return null;
                }
            }

            CheckPermissions(_dataProvider.GetCalendarById(calendarId), CalendarAccessRights.FullAccessAction);

            var todo = _dataProvider.CreateTodo(calendarId,
                                                SecurityContext.CurrentAccount.ID,
                                                name,
                                                description,
                                                utcStartDate,
                                                uid,
                                                completed);

            if (todo != null)
            {

                var todoResult = new TodoWrapper(todo, SecurityContext.CurrentAccount.ID,
                                        _dataProvider.GetTimeZoneForCalendar(SecurityContext.CurrentAccount.ID, calendarId))
                                        .GetList();
                return todoResult;
            }
            return null;
        }
        /// <summary>
        /// Adds an event in the ics format to the calendar specified in the request.
        /// </summary>
        /// <short>
        /// Add the iCal event
        /// </short>
        /// <param name="calendarGuid">Calendar GUID</param>
        /// <param name="eventGuid">Event GUID</param>
        /// <param name="ics">Event in the iCal format</param>
        /// <category>Events</category>
        /// <returns>Event</returns>
        [Create("outsideevent")]
        public async Task AddEventOutside(string calendarGuid, string eventGuid, string ics)
        {

            if (calendarGuid.IndexOf("-readonly") > 0)
            {
                var caldavGuid = calendarGuid.Replace("-readonly", "");

                var calendarTmp = _dataProvider.GetCalendarIdByCaldavGuid(caldavGuid);
                var calendarId = Convert.ToInt32(calendarTmp[0][0]);

                var eventData = _dataProvider.GetEventIdByUid(eventGuid.Split('.')[0], calendarId);

                if (eventData == null)
                {
                    await AddEvent(calendarId, ics, EventAlertType.Never, new List<SharingParam>()).ConfigureAwait(false);
                }
                else
                {
                    if (eventData.OwnerId == SecurityContext.CurrentAccount.ID)
                    {
                        var cal = _dataProvider.GetCalendarById(calendarId);
                        var sharingOptions = eventData.SharingOptions;
                        var eventCharingList = new List<SharingParam>();
                        if (sharingOptions.PublicItems.Count > 1)
                        {
                            eventCharingList.AddRange(from publicItem in sharingOptions.PublicItems
                                                      where publicItem.Id.ToString() != AccessOption.OwnerOption.Id
                                                      select new SharingParam
                                                      {
                                                          Id = publicItem.Id,
                                                          isGroup = publicItem.IsGroup
                                                      });
                        }
                        await UpdateEvent(Convert.ToInt32(eventData.Id), calendarId.ToString(), ics, EventAlertType.Never, eventCharingList).ConfigureAwait(false);
                    }

                }
            }
        }
        /// <summary>
        /// Deletes a project calendar with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Delete a project calendar
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="team">Project team</param>
        /// <category>Calendars and subscriptions</category>
        [Delete("caldavprojcal")]
        public async Task DeleteCaldavCalendar(string calendarId, List<string> team = null)
        {
            try
            {
                var myUri = HttpContext.Current.Request.GetUrlRewriter();
                var caldavHost = myUri.Host;

                var currentTenantId = TenantProvider.CurrentTenantID;
                try
                {
                    foreach (var teamMember in team)
                    {
                        var currentUser = CoreContext.UserManager.GetUsers(Guid.Parse(teamMember));

                        if (CheckUserEmail(currentUser))
                        {
                            var currentUserName = currentUser.Email.ToLower() + "@" + caldavHost;

                            await RemoveCaldavCalendar(
                                currentUser.Email,
                                calendarId,
                                myUri,
                                true).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(String.Format("Delete project caldav calendar: {0}", ex.Message));
            }

        }
        /// <summary>
        /// Deletes the whole CalDav event from the calendar.
        /// </summary>
        /// <short>
        /// Delete the CalDav event
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="uid">Event UID</param>
        /// <param name="responsibles">Task responsibles</param>
        /// <category>Events</category>
        [Delete("caldavevent")]
        public async Task DeleteCaldavEvent(string calendarId, string uid, List<string> responsibles = null)
        {
            try
            {
                var currentUserId = SecurityContext.CurrentAccount.ID;
                var myUri = HttpContext.Current.Request.GetUrlRewriter();
                if (responsibles == null || responsibles.Count == 0)
                {
                    var currentUserEmail = CoreContext.UserManager.GetUsers(currentUserId).Email;
                    await deleteEvent(uid, calendarId, currentUserEmail, myUri, true).ConfigureAwait(false);
                }
                else if (responsibles.Count > 0)
                {
                    var currentTenantId = CoreContext.TenantManager.GetCurrentTenant().TenantId;

                    await DeleteCaldavEventTask(responsibles, myUri, uid, calendarId, currentTenantId).ConfigureAwait(false);

                }
            }
            catch (Exception ex)
            {
                Logger.Error(String.Format("Delete CRM caldav event: {0}", ex.Message));
            }

        }

        public async Task DeleteCaldavEventTask(List<string> responsibles, Uri myUri, string uid, string calendarId, int currentTenantId)
        {
            try
            {
                foreach (var responsibleSid in responsibles)
                {
                    CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
                    var currentUser = CoreContext.UserManager.GetUsers(Guid.Parse(responsibleSid));
                    if (CheckUserEmail(currentUser))
                    {
                        await deleteEvent(
                            uid,
                            calendarId,
                            currentUser.Email,
                            myUri,
                            true
                        ).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
            }
        }




        private Ical.Net.Calendar getEventIcs(int alert, CalendarWrapper calendar, CalendarEvent evt, string calendarId)
        {
            var ddayCalendar = DDayICalParser.ConvertCalendar(calendar.UserCalendar);
            ddayCalendar.Events.Clear();
            evt.Created = null;
            if (calendarId.Contains("Project_"))
            {
                evt.End = new CalDateTime(evt.End.AddDays(1));
            }
            evt.Status = EventStatus.Confirmed.ToString();
            if (alert > 0)
            {
                evt.Alarms.Add(
                    new Alarm()
                    {
                        Action = "DISPLAY",
                        Description = "Reminder",
                        Trigger = new Trigger(TimeSpan.FromMinutes((-1) * alert))
                    }
                );
            }
            ddayCalendar.Events.Add(evt);
            return ddayCalendar;
        }

        /// <summary>
        /// Returns the offset or difference between the time in the specified time zone and Coordinated Universal Time (UTC) for the particular dates.
        /// </summary>
        /// <short>
        /// Get the UTC offset
        /// </short>
        /// <param name="timeZone">Time zone ID</param>
        /// <param name="startDate">Start date to determine the offset</param>
        /// <param name="endDate">End date to determine the offset</param>
        /// <category>Calendars and subscriptions</category>
        /// <returns>The UTC offset in minutes</returns>
        [Create("utcoffset")]
        public object GetUtcOffsets(string timeZone, ApiDateTime startDate, ApiDateTime endDate)
        {
            var timeZoneInfo = TimeZoneConverter.GetTimeZone(timeZone);

            return new
            {
                timeZoneId = TimeZoneConverter.WindowsTzId2OlsonTzId(timeZoneInfo.Id),
                startOffset = timeZoneInfo.GetUtcOffset(startDate).TotalMinutes,
                endOffset = timeZoneInfo.GetUtcOffset(endDate).TotalMinutes
            };
        }

        /// <summary>
        /// Updates the existing CalDav event in the calendar with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Update the CalDav event
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="uid">Event UID</param>
        /// <param name="alert">Event notification type</param>
        /// <param name="responsibles">Task responsibles</param>
        /// <category>Events</category>
        [Update("caldavevent")]
        public async Task UpdateCaldavEvent(string calendarId, string uid, int alert = 0, List<string> responsibles = null)
        {
            try
            {
                if (responsibles.Count > 0)
                {
                    var myUri = HttpContext.Current.Request.GetUrlRewriter();
                    var currentTenantId = CoreContext.TenantManager.GetCurrentTenant().TenantId;


                    var currentUserId = Guid.Empty;
                    if (SecurityContext.IsAuthenticated)
                    {
                        currentUserId = SecurityContext.CurrentAccount.ID;
                        SecurityContext.Logout();
                    }
                    try
                    {

                        var updateCaldavEventTasks = new List<Task>();
                        foreach (var responsibleSid in responsibles)
                        {
                            CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
                            SecurityContext.CurrentUser = Guid.Parse(responsibleSid);

                            var calendarIcs = GetCalendariCalString(_dataProvider, calendarId, true);
                            var parseCalendar = DDayICalParser.DeserializeCalendar(calendarIcs);
                            var calendar = parseCalendar.FirstOrDefault();
                            var sharedCalendar = GetCalendarById(calendarId);

                            if (calendar != null)
                            {
                                var events = calendar.Events;
                                var ddayCalendar = new Ical.Net.Calendar();
                                foreach (var evt in events)
                                {
                                    var eventUid = evt.Uid;
                                    string[] split = eventUid.Split(new Char[] { '@' });
                                    if (uid == split[0])
                                    {
                                        if (sharedCalendar != null)
                                            ddayCalendar = getEventIcs(alert, sharedCalendar, evt, calendarId);
                                    }
                                }
                                var serializeIcs = DDayICalParser.SerializeCalendar(ddayCalendar);


                                var user = CoreContext.UserManager.GetUsers(Guid.Parse(responsibleSid));
                                if (CheckUserEmail(user) && sharedCalendar != null)
                                {
                                    updateCaldavEventTasks.Add(UpdateCaldavEventTask(
                                        serializeIcs,
                                        uid,
                                        true,
                                        calendarId,
                                        myUri,
                                        user.Email,
                                        DateTime.Now,
                                        ddayCalendar.TimeZones[0],
                                        sharedCalendar.UserCalendar.TimeZone, false, true));
                                }

                                SecurityContext.Logout();
                            }

                            await Task.WhenAll(updateCaldavEventTasks).ConfigureAwait(false);
                            CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(String.Format("Error: {0}", ex.Message));
                    }
                    finally
                    {
                        SecurityContext.Logout();
                        if (currentUserId != Guid.Empty)
                        {
                            SecurityContext.CurrentUser = currentUserId;
                        }
                    }

                }
            }
            catch (Exception ex)
            {
                Logger.Error(String.Format("Create/update CRM caldav event: {0}", ex.Message));
            }

        }
        /// <summary>
        /// Creates a new iCal event in the selected calendar with the parameters specified in the request.
        /// </summary>
        /// <short>
        /// Create a new iCal event
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="ics">Event in the iCal format</param>
        /// <param name="alertType">Event notification type</param>
        /// <param name="sharingOptions">Event sharing access parameters</param>
        /// <param name="eventUid">Event UID</param>
        /// <category>Events</category>
        /// <returns>Event</returns>
        [Create("icsevent")]
        public async Task<List<EventWrapper>> AddEvent(int calendarId, string ics, EventAlertType alertType, List<SharingParam> sharingOptions, string eventUid = null)
        {
            var old_ics = ics;
            if (calendarId <= 0)
            {
                var defaultCalendar = LoadInternalCalendars().First(x => (!x.IsSubscription && x.IsTodo != 1));
                if (!int.TryParse(defaultCalendar.Id, out calendarId))
                    throw new Exception(string.Format("Can't parse {0} to int", defaultCalendar.Id));
            }

            var calendars = DDayICalParser.DeserializeCalendar(ics);

            if (calendars == null) return null;

            var calendar = calendars.FirstOrDefault();

            if (calendar == null || calendar.Events == null) return null;

            var eventObj = calendar.Events.FirstOrDefault();

            if (eventObj == null) return null;

            var calendarObj = _dataProvider.GetCalendarById(calendarId);

            if (calendarObj == null) return null;

            var calendarObjViewSettings = calendarObj.ViewSettings == null ? null : calendarObj.ViewSettings.FirstOrDefault();

            var calendarObjTimeZone = calendarObjViewSettings == null ? calendarObj.TimeZone : calendarObjViewSettings.TimeZone;

            var targetCalendar = DDayICalParser.ConvertCalendar(calendarObj.GetUserCalendar(calendarObjViewSettings));

            if (targetCalendar == null) return null;

            var rrule = GetRRuleString(eventObj);

            var eventTimeZone = string.IsNullOrEmpty(eventObj.Start.TzId) ? calendarObjTimeZone : TimeZoneConverter.GetTimeZone(eventObj.Start.TzId);

            if (!string.IsNullOrEmpty(rrule) && eventObj.Start.IsUtc)
            {
                eventTimeZone = calendarObjTimeZone;
            }

            var utcStartDate = eventObj.IsAllDay ? eventObj.Start.Value : DDayICalParser.ToUtc(eventObj.Start);

            var utcEndDate = eventObj.IsAllDay ? eventObj.End.Value : DDayICalParser.ToUtc(eventObj.End);

            if (eventObj.IsAllDay && utcStartDate.Date < utcEndDate.Date)
                utcEndDate = utcEndDate.AddDays(-1);

            eventUid = eventUid == null ? null : string.Format("{0}@{1}", eventUid, DataProvider.EventUidDomain);

            var hasAttachments = eventObj.Attachments != null && eventObj.Attachments.Any();

            var result = CreateEvent(calendarId,
                                     eventObj.Summary,
                                     eventObj.Description,
                                     utcStartDate,
                                     utcEndDate,
                                     RecurrenceRule.Parse(rrule),
                                     alertType,
                                     eventObj.IsAllDay,
                                     sharingOptions,
                                     DataProvider.GetEventUid(eventUid),
                                     EventStatus.Confirmed,
                                     eventObj.Created != null ? eventObj.Created.Value : DateTime.Now,
                                     eventTimeZone,
                                     hasAttachments);

            if (result == null || !result.Any()) return null;

            var evt = result.First();

            eventObj.Uid = evt.Uid;
            eventObj.Sequence = 0;
            eventObj.Status = Ical.Net.EventStatus.Confirmed;

            if (hasAttachments)
            {
                SaveAttachments(eventObj.Attachments, evt.Id);
            }

            targetCalendar.Method = Ical.Net.CalendarMethods.Request;
            targetCalendar.Events.Clear();
            targetCalendar.Events.Add(eventObj);

            ics = DDayICalParser.SerializeCalendar(targetCalendar);

            try
            {
                var uid = evt.Uid;
                string[] split = uid.Split(new Char[] { '@' });

                var calDavGuid = calendarObj != null ? calendarObj.calDavGuid : "";
                var myUri = HttpContext.Current.Request.GetUrlRewriter();
                var userId = SecurityContext.CurrentAccount.ID;
                var currentUserEmail = CoreContext.UserManager.GetUsers(userId).Email.ToLower();

                var currentEventUid = split[0];

                var pic = PublicItemCollection.GetForCalendar(calendarObj);
                var sharingList = new List<SharingParam>();
                if (pic.Items.Count > 1)
                {
                    sharingList.AddRange(from publicItem in pic.Items
                                         where publicItem.ItemId != SecurityContext.CurrentAccount.ID.ToString()
                                         select new SharingParam
                                         {
                                             Id = Guid.Parse(publicItem.ItemId),
                                             isGroup = publicItem.IsGroup,
                                             actionId = publicItem.SharingOption.Id
                                         });
                }
                var currentTenantId = TenantProvider.CurrentTenantID;

                if (!calendarObj.OwnerId.Equals(SecurityContext.CurrentAccount.ID) && CheckPermissions(calendarObj, CalendarAccessRights.FullAccessAction, true))
                {
                    currentEventUid = currentEventUid + "_write";
                }

                _dataProvider.AddEventHistory(calendarId, evt.Uid, int.Parse(evt.Id), ics);

                await UpdateCaldavTask(ics, currentEventUid, calendarObj, myUri, targetCalendar, calendarObjTimeZone, sharingList, split, sharingOptions).ConfigureAwait(false);


            }

            catch (Exception e)
            {
                Logger.Error(e.Message);
            }



            return result;
        }

        /// <summary>
        /// Updates the existing iCal event in the selected calendar with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Update the iCal event
        /// </short>
        /// <param name="eventId">Event ID</param>
        /// <param name="calendarId">Calendar ID</param>
        /// <param name="ics">Event in the iCal format</param>
        /// <param name="alertType">New event notification type</param>
        /// <param name="sharingOptions">New event sharing access parameters</param>
        /// <param name="fromCalDavServer">Defines if the request is from the CalDav server or not</param>
        /// <param name="ownerId">New event owner ID</param>
        /// <category>Events</category>
        /// <returns>Updated event</returns>
        [Update("icsevent")]
        public async Task<List<EventWrapper>> UpdateEvent(int eventId, string calendarId, string ics, EventAlertType alertType, List<SharingParam> sharingOptions, bool fromCalDavServer = false, string ownerId = "")
        {
            var evt = _dataProvider.GetEventById(eventId);

            int oldCalendarId;
            if (int.TryParse(evt.CalendarId, out oldCalendarId))
            {
                oldCalendarId = int.Parse(evt.CalendarId);
            }

            var old_ics = ics;
            if (evt == null)
                throw new Exception(Resources.CalendarApiResource.ErrorItemNotFound);

            var cal = _dataProvider.GetCalendarById(Int32.Parse(evt.CalendarId));
            if (!fromCalDavServer)
            {
                if (!evt.OwnerId.Equals(SecurityContext.CurrentAccount.ID) &&
                    !CheckPermissions(evt, CalendarAccessRights.FullAccessAction, true) &&
                    !CheckPermissions(cal, CalendarAccessRights.FullAccessAction, true))
                    throw new System.Security.SecurityException(Resources.CalendarApiResource.ErrorAccessDenied);
            }
            int calId;

            if (!int.TryParse(calendarId, out calId))
            {
                calId = int.Parse(evt.CalendarId);
            }

            EventHistory evtHistory = null;

            if (string.IsNullOrEmpty(evt.Uid))
            {
                evt.Uid = DataProvider.GetEventUid(evt.Uid);
                _dataProvider.SetEventUid(eventId, evt.Uid);
            }
            else
            {
                evtHistory = _dataProvider.GetEventHistory(eventId);
            }

            var sequence = 0;
            if (evtHistory != null)
            {
                var maxSequence = evtHistory.History.Select(x => x.Events.First()).Max(x => x.Sequence);
                if (!fromCalDavServer)
                {
                    if (evt.OwnerId == SecurityContext.CurrentAccount.ID && !CheckIsOrganizer(evtHistory))
                        sequence = maxSequence;
                    else
                        sequence = maxSequence + 1;
                }
            }

            var calendars = DDayICalParser.DeserializeCalendar(ics);

            if (calendars == null) return null;

            var calendar = calendars.FirstOrDefault();

            if (calendar == null || calendar.Events == null) return null;

            var eventObj = calendar.Events.FirstOrDefault();

            if (eventObj == null) return null;

            var calendarObj = _dataProvider.GetCalendarById(calId);

            if (calendarObj == null) return null;

            var calendarObjViewSettings = calendarObj.ViewSettings == null ? null : calendarObj.ViewSettings.FirstOrDefault();

            var calendarObjTimeZone = calendarObjViewSettings == null ? calendarObj.TimeZone : calendarObjViewSettings.TimeZone;

            var targetCalendar = DDayICalParser.ConvertCalendar(calendarObj.GetUserCalendar(calendarObjViewSettings));

            if (targetCalendar == null) return null;

            eventObj.Uid = evt.Uid;
            eventObj.Sequence = sequence;
            //eventObj.ExceptionDates.Clear();

            var hasAttachments = eventObj.Attachments != null && eventObj.Attachments.Any();

            if (hasAttachments)
            {
                var currentAttachments = AttachmentEngine.GetFiles(evt.Id).Select(x => x.ID.ToString());
                var actualAttachments = eventObj.Attachments.Select(x => GetFileIdFromUriQuery(x.Uri));

                var newAttachments = actualAttachments.Except(currentAttachments);
                var removedAttachments = currentAttachments.Except(actualAttachments);

                var baseUrl = CommonLinkUtility.GetFullAbsolutePath(FilesLinkUtility.FilesBaseAbsolutePath);
                foreach (var attachment in eventObj.Attachments)
                {
                    if (!attachment.Uri.AbsoluteUri.StartsWith(baseUrl))
                    {
                        continue;
                    }
                    var fileId = GetFileIdFromUriQuery(attachment.Uri);
                    if (newAttachments.Contains(fileId))
                    {
                        SaveAttachment(attachment, evt.Id);
                    }
                }

                foreach (var fileId in removedAttachments)
                {
                    AttachmentEngine.DeleteFile(fileId);
                }
            }

            targetCalendar.Method = fromCalDavServer ? calendar.Method : Ical.Net.CalendarMethods.Request;
            targetCalendar.Events.Clear();
            targetCalendar.Events.Add(eventObj);

            ics = (evtHistory != null ? (evtHistory.Ics + Environment.NewLine) : string.Empty) + DDayICalParser.SerializeCalendar(targetCalendar);

            _dataProvider.RemoveEventHistory(eventId);

            evtHistory = _dataProvider.AddEventHistory(calId, evt.Uid, eventId, ics);

            var mergedCalendar = evtHistory.GetMerged();

            if (mergedCalendar == null || mergedCalendar.Events == null || !mergedCalendar.Events.Any()) return null;

            var mergedEvent = mergedCalendar.Events.First();

            var rrule = GetRRuleString(mergedEvent);

            var eventTimeZone = string.IsNullOrEmpty(eventObj.Start.TzId) ? calendarObjTimeZone : TimeZoneConverter.GetTimeZone(eventObj.Start.TzId);

            if (!string.IsNullOrEmpty(rrule) && eventObj.Start.IsUtc)
            {
                eventTimeZone = calendarObjTimeZone;
            }

            var utcStartDate = eventObj.IsAllDay ? eventObj.Start.Value : DDayICalParser.ToUtc(eventObj.Start);

            var utcEndDate = eventObj.IsAllDay ? eventObj.End.Value : DDayICalParser.ToUtc(eventObj.End);

            var createDate = mergedEvent.Created != null ? mergedEvent.Created.Value : DateTime.Now;

            if (eventObj.IsAllDay && utcStartDate.Date < utcEndDate.Date)
                utcEndDate = utcEndDate.AddDays(-1);


            var resultEvent = UpdateEvent(calendarId,
                               eventId,
                               eventObj.Summary,
                               eventObj.Description,
                               new ApiDateTime(utcStartDate, TimeZoneInfo.Utc),
                               new ApiDateTime(utcEndDate, TimeZoneInfo.Utc),
                               rrule,
                               alertType,
                               eventObj.IsAllDay,
                               sharingOptions,
                               DDayICalParser.ConvertEventStatus(mergedEvent.Status), createDate,
                               hasAttachments,
                               fromCalDavServer,
                               ownerId,
                               eventTimeZone);

            if (!fromCalDavServer)
            {
                try
                {
                    var uid = evt.Uid;
                    string[] uidData = uid.Split(new Char[] { '@' });

                    var calDavGuid = calendarObj != null ? calendarObj.calDavGuid : "";
                    var myUri = HttpContext.Current.Request.GetUrlRewriter();
                    var currentUserEmail = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID).Email.ToLower();

                    var isFullAccess = SecurityContext.PermissionResolver.Check(SecurityContext.CurrentAccount, evt, null,
                                                                              CalendarAccessRights.FullAccessAction);
                    var isShared = false;
                    if (calendarId == BirthdayReminderCalendar.CalendarId ||
                        calendarId == SharedEventsCalendar.CalendarId ||
                        calendarId == "crm_calendar")
                    {
                        calDavGuid = calendarId;
                        isShared = true;
                    }
                    else
                    {
                        isShared = calendarObj != null && calendarObj.OwnerId != SecurityContext.CurrentAccount.ID;
                    }

                    var eventUid = isShared && isFullAccess ? uidData[0] + "_write" : uidData[0];

                    var currentTenantId = TenantProvider.CurrentTenantID;
                    var pic = PublicItemCollection.GetForCalendar(cal);
                    var calendarCharingList = new List<SharingParam>();
                    if (pic.Items.Count > 1)
                    {
                        calendarCharingList.AddRange(from publicItem in pic.Items
                                                     where publicItem.ItemId != calendarObj.OwnerId.ToString()
                                                     select new SharingParam
                                                     {
                                                         Id = Guid.Parse(publicItem.ItemId),
                                                         isGroup = publicItem.IsGroup,
                                                         actionId = publicItem.SharingOption.Id
                                                     });
                    }

                    await SharingEventTask(old_ics, myUri, calendarObj, targetCalendar, calendarObjTimeZone, sharingOptions, uidData, calendarId,
            createDate, calendarCharingList, cal, isShared, eventUid, oldCalendarId != 0 && oldCalendarId.ToString() != calendarId ? _dataProvider.GetCalendarById(oldCalendarId).calDavGuid : "").ConfigureAwait(false);

                }
                catch (Exception e)
                {
                    Logger.Error(e.Message);
                }
            }

            return resultEvent;
        }

        private void SaveAttachment(Attachment attachment, string eventId)
        {
            var query = HttpUtility.ParseQueryString(attachment.Uri.Query);
            var fileId = query["fileId"];

            if (!string.IsNullOrEmpty(query["tmp"]))
            {
                var folderID = AttachmentEngine.GetFolderId(eventId);
                AttachmentEngine.MoveFile(fileId, folderID);

                attachment.Uri = AttachmentEngine.GetUri(fileId);
            }
            else
            {
                if (string.IsNullOrEmpty(query["doc"]))
                {
                    attachment.Uri = AttachmentEngine.ShareFileAndGetUri(fileId);
                }
            }
        }

        private string GetFileIdFromUriQuery(Uri uri)
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            return query["fileid"];
        }

        private void SaveAttachmentsAndChangeUri(IEnumerable<HttpPostedFileBase> docs, IList<Attachment> attachments)
        {
            var folderId = AttachmentEngine.GetTmpFolderId();

            foreach (var attachment in attachments)
            {
                if (attachment.Uri.AbsoluteUri.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                {
                    var cid = attachment.Uri.AbsoluteUri.Replace("cid:", "");
                    var doc = docs.FirstOrDefault(x => x.FileName.StartsWith(cid));
                    if (doc != null)
                    {
                        var cidFileName = doc.FileName.Split('/');
                        var fileName = cidFileName[1];
                        var document = new Files.Core.File
                        {
                            Title = fileName,
                            FolderID = folderId,
                            ContentLength = doc.ContentLength,
                            ThumbnailStatus = Files.Core.Thumbnail.NotRequired
                        };
                        document = AttachmentEngine.SaveFile(document, doc.InputStream);

                        var uriString = AttachmentEngine.GetUriString(document) + "&tmp=true";
                        attachment.Uri = new Uri(uriString);
                        attachment.Parameters.Add("FILENAME", document.Title);
                    }
                }
            }
        }

        private void SaveAttachments(IList<Attachment> attachments, string eventId)
        {
            var baseUrl = CommonLinkUtility.GetFullAbsolutePath(FilesLinkUtility.FilesBaseAbsolutePath);
            foreach (var attachment in attachments)
            {
                if (attachment.Uri.AbsoluteUri.StartsWith(baseUrl))
                {
                    SaveAttachment(attachment, eventId);
                }
            }
        }

        private static async Task UpdateSharedEvent(Core.Users.UserInfo userSharingInfo, string guid, bool fullAccess,
                            Uri myUri,
                            string oldIcs,
                            string calendarId,
                            DateTime updateDate = default(DateTime),
                            VTimeZone calendarVTimeZone = null,
                            TimeZoneInfo calendarTimeZone = null)
        {
            string eventUid = guid,
                   oldEventUid = guid;

            if (fullAccess)
            {
                eventUid = guid + "_write";
                oldEventUid = guid;
            }
            else
            {
                oldEventUid = guid + "_write";
                eventUid = guid;
            }

            var calDavServerUrl = myUri.Scheme + "://" + myUri.Host + "/caldav";
            var caldavHost = myUri.Host;

            Logger.Info("RADICALE REWRITE URL: " + myUri);

            var currentUserName = userSharingInfo.Email.ToLower() + "@" + caldavHost;

            var requestDeleteUrl = calDavServerUrl + "/" + HttpUtility.UrlEncode(currentUserName) + "/" + calendarId + "-readonly" + "/" + oldEventUid + ".ics";

            updatedEvents.Add(guid);

            try
            {
                var calDavCalendar = new CalDavCalendar(calendarId, false);
                var authorization = calDavCalendar.GetSystemAuthorization();
                var davRequest = new DavRequest()
                {
                    Url = requestDeleteUrl,
                    Authorization = authorization
                };
                await RadicaleClient.RemoveAsync(davRequest).ConfigureAwait(false);
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.ProtocolError && ex.Response != null)
                {
                    var resp = (HttpWebResponse)ex.Response;
                    if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Conflict)
                        Logger.Debug("ERROR: " + ex.Message);
                    else
                        Logger.Error("ERROR: " + ex.Message);
                }
                else
                {
                    Logger.Error("ERROR: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
            finally
            {
                await UpdateCaldavEventTask(oldIcs, eventUid, true, calendarId, myUri,
                                  userSharingInfo.Email, updateDate, calendarVTimeZone, calendarTimeZone, false, true).ConfigureAwait(false);
            }
        }
        private static async Task ReplaceSharingEvent(
                            ASC.Core.Users.UserInfo user,
                            string actionId,
                            string guid,
                            Uri myUri,
                            string oldIcs,
                            string calendarId,
                            DateTime updateDate = default(DateTime),
                            VTimeZone calendarVTimeZone = null,
                            TimeZoneInfo calendarTimeZone = null,
                            string calGuid = null)
        {
            if (calGuid != "" && myUri != null && user != null)
            {
                string eventUid = guid,
                    oldEventUid = guid;
                if (actionId == AccessOption.FullAccessOption.Id)
                {
                    eventUid = guid + "_write";
                    oldEventUid = guid;
                }
                else if (actionId != AccessOption.OwnerOption.Id)
                {
                    oldEventUid = guid + "_write";
                    eventUid = guid;
                }

                var calDavServerUrl = myUri.Scheme + "://" + myUri.Host + "/caldav";
                var caldavHost = myUri.Host;

                Logger.Info("RADICALE REWRITE URL: " + myUri);

                var currentUserName = user.Email.ToLower() + "@" + caldavHost;

                var requestDeleteUrl = calDavServerUrl + "/" + HttpUtility.UrlEncode(currentUserName) + "/" +
                    (calGuid ?? SharedEventsCalendar.CalendarId) + (actionId != AccessOption.OwnerOption.Id ? "-readonly" : "") + "/" + oldEventUid +
                                        ".ics";

                try
                {
                    var calDavCalendar = new CalDavCalendar(calendarId, false);
                    var authorization = calDavCalendar.GetSystemAuthorization();
                    var davRequest = new DavRequest()
                    {
                        Url = requestDeleteUrl,
                        Authorization = authorization
                    };
                    await RadicaleClient.RemoveAsync(davRequest).ConfigureAwait(false);
                }
                catch (WebException ex)
                {
                    if (ex.Status == WebExceptionStatus.ProtocolError && ex.Response != null)
                    {
                        var resp = (HttpWebResponse)ex.Response;
                        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Conflict)
                            Logger.Debug("ERROR: " + ex.Message);
                        else
                            Logger.Error("ERROR: " + ex.Message);
                    }
                    else
                    {
                        Logger.Error("ERROR: " + ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("ERROR: " + ex.Message);
                }
                finally
                {
                    await UpdateCaldavEventTask(oldIcs, eventUid, true,
                                        (calGuid ?? SharedEventsCalendar.CalendarId), myUri,
                                        user.Email, updateDate, calendarVTimeZone, calendarTimeZone, false, actionId != AccessOption.OwnerOption.Id).ConfigureAwait(false);
                }
            }

        }



        private static async Task updateEvent(string ics, string uid, string calendarId, bool sendToRadicale, DateTime updateDate = default(DateTime), Ical.Net.CalendarComponents.VTimeZone calendarVTimeZone = null, TimeZoneInfo calendarTimeZone = null)
        {
            using (var db = DbManager.FromHttpContext("calendar"))
            {
                using (var tr = db.BeginTransaction())
                {
                    if (sendToRadicale)
                    {
                        try
                        {
                            var dataCaldavGuid =
                                db.ExecuteList(new SqlQuery("calendar_calendars")
                                  .Select("caldav_guid")
                                  .Where("id", calendarId))
                                  .Select(r => r[0])
                                  .ToArray();
                            var caldavGuid = dataCaldavGuid[0] != null
                                                 ? Guid.Parse(dataCaldavGuid[0].ToString())
                                                 : Guid.Empty;

                            if (caldavGuid != Guid.Empty)
                            {

                                var myUri = HttpContext.Current.Request.GetUrlRewriter();

                                var calDavServerUrl = myUri.Scheme + "://" + myUri.Host + "/caldav";
                                var caldavHost = myUri.Host;

                                Logger.Info("RADICALE REWRITE URL: " + myUri);

                                var currentUserName = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID).Email.ToLower() + "@" + caldavHost;
                                var _email = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID).Email;

                                int indexOfChar = ics.IndexOf("BEGIN:VTIMEZONE");
                                int indexOfCharEND = ics.IndexOf("END:VTIMEZONE");

                                if (indexOfChar != -1)
                                {
                                    ics = ics.Remove(indexOfChar, indexOfCharEND + 14 - indexOfChar);
                                    if (ics.IndexOf("BEGIN:VTIMEZONE") > -1) await updateEvent(ics, uid, calendarId, true).ConfigureAwait(false);
                                }

                                var requestUrl = calDavServerUrl + "/" + HttpUtility.UrlEncode(currentUserName) + "/" + caldavGuid +
                                                 "/" + uid + ".ics";
                                if (calendarTimeZone != null && calendarVTimeZone != null)
                                {
                                    var icsCalendars = DDayICalParser.DeserializeCalendar(ics);
                                    var icsCalendar = icsCalendars == null ? null : icsCalendars.FirstOrDefault();
                                    var icsEvents = icsCalendar == null ? null : icsCalendar.Events;
                                    var icsEvent = icsEvents == null ? null : icsEvents.FirstOrDefault();
                                    if (icsEvent != null && !icsEvent.IsAllDay)
                                    {
                                        var tz = TimeZoneConverter.GetTimeZone(calendarVTimeZone.TzId);

                                        if (icsEvent.DtStart.TzId != calendarVTimeZone.TzId)
                                        {
                                            var _DtStart = DDayICalParser.ToUtc(icsEvent.Start).Add(tz.GetUtcOffset(icsEvent.Start.Value));
                                            icsEvent.DtStart = new CalDateTime(_DtStart.Year, _DtStart.Month, _DtStart.Day, _DtStart.Hour, _DtStart.Minute, _DtStart.Second, calendarVTimeZone.TzId);
                                        }

                                        if (icsEvent.DtEnd.TzId != calendarVTimeZone.TzId)
                                        {
                                            var _DtEnd = DDayICalParser.ToUtc(icsEvent.End).Add(tz.GetUtcOffset(icsEvent.End.Value));
                                            icsEvent.DtEnd = new CalDateTime(_DtEnd.Year, _DtEnd.Month, _DtEnd.Day, _DtEnd.Hour, _DtEnd.Minute, _DtEnd.Second, calendarVTimeZone.TzId);
                                        }

                                        icsEvent.Uid = null;

                                        ics = DDayICalParser.SerializeCalendar(icsCalendar);
                                    }
                                }


                                try
                                {
                                    var calDavCalendar = new CalDavCalendar(caldavGuid.ToString(), false);
                                    var authorization = GetUserAuthorization(_email);
                                    await calDavCalendar.UpdateItem(requestUrl, authorization, ics).ConfigureAwait(false);
                                }
                                catch (WebException ex)
                                {
                                    if (ex.Status == WebExceptionStatus.ProtocolError && ex.Response != null)
                                    {
                                        var resp = (HttpWebResponse)ex.Response;
                                        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Conflict)
                                            Logger.Debug("ERROR: " + ex.Message);
                                        else
                                            Logger.Error("ERROR: " + ex.Message);
                                    }
                                    else
                                    {
                                        Logger.Error("ERROR: " + ex.Message);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error("ERROR: " + ex.Message);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("ERROR: " + ex.Message);
                        }
                    }
                }
            }

        }

        public enum EventRemoveType
        {
            Single = 0,
            AllFollowing = 1,
            AllSeries = 2
        }

        /// <summary>
        /// Deletes the event series from the calendar.
        /// </summary>
        /// <short>
        /// Delete the event series
        /// </short>
        /// <param name="eventId">Event ID</param>
        /// <category>Events</category>
        [Delete("events/{eventId}")]
        public async Task RemoveEvent(int eventId)
        {
            await RemoveEvent(eventId, null, EventRemoveType.AllSeries).ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes one event from the series of recurrent events.
        /// </summary>
        /// <short>
        /// Delete an event from event series
        /// </short>
        /// <param name="eventId">Event ID</param>
        /// <param name="date">Date to be deleted from the recurrent event</param>
        /// <param name="type">Recurrent event deletion type</param>
        /// <param name="fromCaldavServer">Defines if the request is from the CalDav server or not</param>
        /// <param name="uri" visible="false">Current URI</param>
        /// <category>Events</category>
        /// <returns>Updated event series collection</returns>
        [Delete("events/{eventId}/custom")]
        public async Task<List<EventWrapper>> RemoveEvent(int eventId, ApiDateTime date, EventRemoveType type, Uri uri = null, bool fromCaldavServer = false)
        {
            var events = new List<EventWrapper>();
            var evt = _dataProvider.GetEventById(eventId);

            if (evt == null)
                throw new Exception(Resources.CalendarApiResource.ErrorItemNotFound);

            var cal = _dataProvider.GetCalendarById(Convert.ToInt32(evt.CalendarId));
            var calTz = cal.ViewSettings.Any() && cal.ViewSettings.First().TimeZone != null ? cal.ViewSettings.First().TimeZone : cal.TimeZone;
            var pic = PublicItemCollection.GetForCalendar(cal);

            var uid = evt.Uid;
            string[] split = uid.Split(new Char[] { '@' });

            var sharingList = new List<SharingParam>();
            if (pic.Items.Count > 1)
            {
                sharingList.AddRange(from publicItem in pic.Items
                                     where publicItem.ItemId != cal.OwnerId.ToString()
                                     select new SharingParam
                                     {
                                         Id = Guid.Parse(publicItem.ItemId),
                                         isGroup = publicItem.IsGroup,
                                         actionId = publicItem.SharingOption.Id
                                     });
            }
            var permissions = PublicItemCollection.GetForEvent(evt);
            var so = permissions.Items
                .Where(x => x.SharingOption.Id != AccessOption.OwnerOption.Id)
                .Select(x => new SharingParam
                {
                    Id = x.Id,
                    actionId = x.SharingOption.Id,
                    isGroup = x.IsGroup
                }).ToList();

            var currentTenantId = TenantProvider.CurrentTenantID;
            var calendarId = evt.CalendarId;
            var myUri = HttpContext.Current != null ? HttpContext.Current.Request.GetUrlRewriter() : uri != null ? uri : new Uri("http://localhost");
            var currentUserId = SecurityContext.CurrentAccount.ID;



            if (evt.OwnerId.Equals(SecurityContext.CurrentAccount.ID) || CheckPermissions(evt, CalendarAccessRights.FullAccessAction, true) || CheckPermissions(cal, CalendarAccessRights.FullAccessAction, true))
            {
                if (type == EventRemoveType.AllSeries || evt.RecurrenceRule.Freq == Frequency.Never)
                {
                    CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
                    _dataProvider.RemoveEvent(eventId);
                    AttachmentEngine.DeleteFolder(eventId.ToString());

                    var ownerId = SecurityContext.CurrentAccount.ID != cal.OwnerId ? cal.OwnerId : SecurityContext.CurrentAccount.ID;
                    var email = CoreContext.UserManager.GetUsers(ownerId).Email;
                    await deleteEvent(split[0], evt.CalendarId, email, myUri).ConfigureAwait(false);
                    try
                    {
                        CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
                        //calendar sharing list
                        foreach (var sharingOption in sharingList)
                        {
                            var fullAccess = sharingOption.actionId == AccessOption.FullAccessOption.Id;

                            if (!sharingOption.IsGroup)
                            {
                                var user = CoreContext.UserManager.GetUsers(sharingOption.itemId);
                                if (CheckUserEmail(user))
                                {
                                    await deleteEvent(fullAccess ? split[0] + "_write" : split[0], calendarId, user.Email, myUri, user.ID != cal.OwnerId).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                var users = CoreContext.UserManager.GetUsersByGroup(sharingOption.itemId);
                                foreach (var user in users)
                                {
                                    if (CheckUserEmail(user))
                                    {
                                        var eventUid = user.ID == evt.OwnerId
                                                        ? split[0]
                                                        : fullAccess ? split[0] + "_write" : split[0];
                                        await deleteEvent(eventUid, calendarId, user.Email, myUri, true).ConfigureAwait(false);
                                    }

                                }
                            }
                        }
                        //event sharing list
                        foreach (var sharingOption in so)
                        {
                            var fullAccess = sharingOption.actionId == AccessOption.FullAccessOption.Id;

                            if (!sharingOption.IsGroup)
                            {
                                var user = CoreContext.UserManager.GetUsers(sharingOption.itemId);
                                await deleteEvent(fullAccess ? split[0] + "_write" : split[0], SharedEventsCalendar.CalendarId, user.Email, myUri, user.ID != evt.OwnerId).ConfigureAwait(false);
                            }
                            else
                            {
                                var users = CoreContext.UserManager.GetUsersByGroup(sharingOption.itemId);
                                foreach (var user in users)
                                {
                                    var eventUid = user.ID == evt.OwnerId
                                                        ? split[0]
                                                        : fullAccess ? split[0] + "_write" : split[0];

                                    await deleteEvent(eventUid, SharedEventsCalendar.CalendarId, user.Email, myUri, true).ConfigureAwait(false);
                                }
                            }
                        }
                        if (currentUserId == evt.OwnerId)
                        {
                            var owner = CoreContext.UserManager.GetUsers(evt.OwnerId);
                            await deleteEvent(split[0], evt.CalendarId, owner.Email, myUri).ConfigureAwait(false);
                        }
                        if (calendarId != BirthdayReminderCalendar.CalendarId &&
                                calendarId != SharedEventsCalendar.CalendarId &&
                                calendarId != "crm_calendar" &&
                                !calendarId.Contains("Project_"))
                        {
                            if (currentUserId == cal.OwnerId)
                            {
                                CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
                                var owner = CoreContext.UserManager.GetUsers(currentUserId);

                                await deleteEvent(split[0], evt.CalendarId, owner.Email, myUri).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
                    }

                    return events;
                }

                var eventTimeZone = evt.TimeZone ?? calTz;
                var eventTimeZoneId = TimeZoneConverter.WindowsTzId2OlsonTzId(eventTimeZone.Id);
                var utcDate = evt.AllDayLong
                                  ? date.UtcTime.Date
                                  : TimeZoneInfo.ConvertTime(new DateTime(date.UtcTime.Ticks),
                                                             eventTimeZone,
                                                             TimeZoneInfo.Utc);

                if (type == EventRemoveType.Single)
                {
                    evt.RecurrenceRule.ExDates.Add(new RecurrenceRule.ExDate
                    {
                        Date = evt.AllDayLong ? utcDate.Date : utcDate,
                        isDateTime = !evt.AllDayLong
                    });
                }
                else if (type == EventRemoveType.AllFollowing)
                {
                    var lastEventDate = evt.AllDayLong ? utcDate.Date : utcDate;
                    var dates = evt.RecurrenceRule
                        .GetDates(evt.UtcStartDate, eventTimeZone, evt.AllDayLong, evt.UtcStartDate, evt.UtcStartDate.AddMonths(_monthCount), int.MaxValue, false)
                        .Where(x => x < lastEventDate)
                        .ToList();

                    var untilDate = dates.Any() ? dates.Last() : evt.UtcStartDate.AddDays(-1);

                    evt.RecurrenceRule.Until = evt.AllDayLong ? untilDate.Date : untilDate;
                }

                evt = _dataProvider.UpdateEvent(int.Parse(evt.Id), int.Parse(evt.CalendarId), evt.OwnerId, evt.Name, evt.Description,
                                              evt.UtcStartDate, evt.UtcEndDate, evt.RecurrenceRule, evt.AlertType, evt.AllDayLong,
                                              evt.SharingOptions.PublicItems, evt.Status, DateTime.Now, eventTimeZone, evt.HasAttachments);
                if (!fromCaldavServer)
                {
                    try
                    {
                        var calDavGuid = cal != null ? cal.calDavGuid : "";
                        var currentUserEmail = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID).Email.ToLower();

                        var calendarObj = _dataProvider.GetCalendarById(Convert.ToInt32(cal.Id));
                        var calendarObjViewSettings = calendarObj != null && calendarObj.ViewSettings != null ? calendarObj.ViewSettings.FirstOrDefault() : null;
                        var targetCalendar = DDayICalParser.ConvertCalendar(calendarObj != null ? calendarObj.GetUserCalendar(calendarObjViewSettings) : null);

                        targetCalendar.Events.Clear();

                        var convertedEvent = DDayICalParser.ConvertEvent(evt, eventTimeZone);

                        targetCalendar.Events.Add(convertedEvent);

                        var ics = DDayICalParser.SerializeCalendar(targetCalendar);


                        {
                            try
                            {
                                CoreContext.TenantManager.SetCurrentTenant(currentTenantId);
                                await UpdateCaldavEventTask(ics, split[0], true, calDavGuid, myUri, currentUserEmail, DateTime.Now, targetCalendar.TimeZones[0], calTz, true).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                LogManager.GetLogger("ASC.Calendar").Error(ex.Message);
                            }

                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e.Message);
                    }


                }


                if (type != EventRemoveType.AllSeries)
                {
                    var history = _dataProvider.GetEventHistory(eventId);
                    if (history != null)
                    {
                        var mergedCalendar = history.GetMerged();
                        if (mergedCalendar != null && mergedCalendar.Events != null && mergedCalendar.Events.Any())
                        {
                            if (evt.OwnerId != SecurityContext.CurrentAccount.ID || CheckIsOrganizer(history))
                            {
                                mergedCalendar.Events[0].Sequence++;
                            }

                            mergedCalendar.Events[0].RecurrenceRules.Clear();

                            mergedCalendar.Events[0].RecurrenceRules.Add(DDayICalParser.DeserializeRecurrencePattern(evt.RecurrenceRule.ToString(true)));

                            mergedCalendar.Events[0].ExceptionDates = DDayICalParser.GetExceptionDates(evt, eventTimeZone, eventTimeZoneId);

                            _dataProvider.AddEventHistory(int.Parse(evt.CalendarId), evt.Uid, int.Parse(evt.Id), DDayICalParser.SerializeCalendar(mergedCalendar));
                        }
                    }
                }

                //define timeZone
                TimeZoneInfo timeZone;
                if (!CheckPermissions(cal, CalendarAccessRights.FullAccessAction, true))
                {
                    timeZone = _dataProvider.GetTimeZoneForSharedEventsCalendar(SecurityContext.CurrentAccount.ID);
                    evt.CalendarId = SharedEventsCalendar.CalendarId;
                }
                else
                    timeZone = _dataProvider.GetTimeZoneForCalendar(SecurityContext.CurrentAccount.ID, int.Parse(evt.CalendarId));

                events = new EventWrapper(evt, SecurityContext.CurrentAccount.ID, timeZone).GetList(evt.UtcStartDate, evt.UtcStartDate.AddMonths(_monthCount));
            }
            else
                _dataProvider.UnsubscribeFromEvent(eventId, SecurityContext.CurrentAccount.ID);

            return events;
        }

        private static async Task deleteEvent(string uid, string calendarId, string email, Uri myUri, bool isShared = false)
        {
            using (var db = DbManager.FromHttpContext("calendar"))
            {
                using (var tr = db.BeginTransaction())
                {
                    try
                    {
                        var caldavGuid = "";
                        if (calendarId != BirthdayReminderCalendar.CalendarId &&
                            calendarId != SharedEventsCalendar.CalendarId &&
                            calendarId != "crm_calendar" &&
                            !calendarId.Contains("Project_"))
                        {
                            var dataCaldavGuid = db.ExecuteList(new SqlQuery("calendar_calendars")
                                .Select("caldav_guid")
                                .Where("id", calendarId))
                                .Select(r => r[0])
                                .ToArray();

                            caldavGuid = dataCaldavGuid[0].ToString();
                        }
                        else
                        {
                            caldavGuid = calendarId;
                        }

                        if (caldavGuid != "")
                        {

                            Logger.Info("RADICALE REWRITE URL: " + myUri);


                            try
                            {
                                var calDavCalendar = new CalDavCalendar(calendarId, false);
                                var authorization = isShared ? calDavCalendar.GetSystemAuthorization() : GetUserAuthorization(email);
                                var requestUrl = calDavCalendar.GetRadicaleUrl(myUri.ToString(), email, isShared, false, true, caldavGuid, uid);
                                var davRequest = new DavRequest()
                                {
                                    Url = requestUrl,
                                    Authorization = authorization
                                };
                                await RadicaleClient.RemoveAsync(davRequest).ConfigureAwait(false);
                            }
                            catch (WebException ex)
                            {
                                if (ex.Status == WebExceptionStatus.ProtocolError && ex.Response != null)
                                {
                                    var resp = (HttpWebResponse)ex.Response;
                                    if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Conflict)
                                        Logger.Debug("ERROR: " + ex.Message);
                                    else
                                        Logger.Error("ERROR: " + ex.Message);
                                }
                                else
                                {
                                    Logger.Error("ERROR: " + ex.Message);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error("ERROR: " + ex.Message);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("ERROR: " + ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// Unsubscribes the current user from the event with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Unsubscribe from the event
        /// </short>
        /// <param name="eventId">Event ID</param>
        /// <category>Events</category>
        [Delete("events/{eventId}/unsubscribe")]
        public async Task UnsubscribeEvent(int eventId)
        {
            var evt = _dataProvider.GetEventById(eventId);

            if (evt != null)
            {
                string[] split = evt.Uid.Split(new Char[] { '@' });
                var myUri = HttpContext.Current.Request.GetUrlRewriter();
                var email = CoreContext.UserManager.GetUsers(SecurityContext.CurrentAccount.ID).Email;
                var fullAccess = CheckPermissions(evt, CalendarAccessRights.FullAccessAction, true);

                await deleteEvent(fullAccess ? split[0] + "_write" : split[0], SharedEventsCalendar.CalendarId, email, myUri, SecurityContext.CurrentAccount.ID != evt.OwnerId).ConfigureAwait(false);

                _dataProvider.UnsubscribeFromEvent(eventId, SecurityContext.CurrentAccount.ID);
            }
        }

        /// <summary>
        /// Returns an event in the iCal format by its UID from the history.
        /// </summary>
        /// <short>
        /// Get ics by UID
        /// </short>
        /// <param name="eventUid">Event UID</param>
        /// <category>Events</category>
        /// <returns>Event history</returns>
        [Read("events/{eventUid}/historybyuid")]
        public EventHistoryWrapper GetEventHistoryByUid(string eventUid)
        {
            if (string.IsNullOrEmpty(eventUid))
            {
                throw new ArgumentException("eventUid");
            }

            var evt = _dataProvider.GetEventByUid(eventUid);

            return GetEventHistoryWrapper(evt);
        }

        /// <summary>
        /// Returns an event in the iCal format by its ID from the history.
        /// </summary>
        /// <short>
        /// Get ics by ID
        /// </short>
        /// <param name="eventId">Event ID</param>
        /// <category>Events</category>
        /// <returns>Event history</returns>
        [Read("events/{eventId}/historybyid")]
        public EventHistoryWrapper GetEventHistoryById(int eventId)
        {
            if (eventId <= 0)
            {
                throw new ArgumentException("eventId");
            }

            var evt = _dataProvider.GetEventById(eventId);

            return GetEventHistoryWrapper(evt);
        }

        #endregion

        private EventHistoryWrapper GetEventHistoryWrapper(Event evt, bool fullHistory = false)
        {
            if (evt == null) return null;

            int calId;
            BusinessObjects.Calendar cal = null;

            if (int.TryParse(evt.CalendarId, out calId))
                cal = _dataProvider.GetCalendarById(calId);

            if (cal == null) return null;

            int evtId;
            EventHistory history = null;

            if (int.TryParse(evt.Id, out evtId))
                history = _dataProvider.GetEventHistory(evtId);

            if (history == null) return null;

            return ToEventHistoryWrapper(evt, cal, history, fullHistory);
        }
        private EventHistoryWrapper ToEventHistoryWrapper(Event evt, BusinessObjects.Calendar cal, EventHistory history, bool fullHistory = false)
        {
            var canNotify = false;
            bool canEdit;

            var calIsShared = cal.SharingOptions.SharedForAll || cal.SharingOptions.PublicItems.Count > 0;
            if (calIsShared)
            {
                canEdit = canNotify = CheckPermissions(cal, CalendarAccessRights.FullAccessAction, true);
                return new EventHistoryWrapper(history, canEdit, canNotify, cal, fullHistory);
            }

            var evtIsShared = evt.SharingOptions.SharedForAll || evt.SharingOptions.PublicItems.Count > 0;
            if (evtIsShared)
            {
                canEdit = canNotify = CheckPermissions(evt, CalendarAccessRights.FullAccessAction, true);
                return new EventHistoryWrapper(history, canEdit, canNotify, cal, fullHistory);
            }

            canEdit = CheckPermissions(evt, CalendarAccessRights.FullAccessAction, true);
            if (canEdit)
            {
                canNotify = CheckIsOrganizer(history);
            }

            return new EventHistoryWrapper(history, canEdit, canNotify, cal, fullHistory);
        }
        private bool CheckIsOrganizer(EventHistory history)
        {
            var canNotify = false;

            var apiServer = new ApiServer();
            var apiResponse = apiServer.GetApiResponse(String.Format("{0}mail/accounts.json", SetupInfo.WebApiBaseUrl), "GET");
            var obj = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(apiResponse)));

            if (obj["response"] != null)
            {
                var accounts = (from account in JArray.Parse(obj["response"].ToString())
                                let email = account.Value<String>("email")
                                let enabled = account.Value<Boolean>("enabled")
                                let isGroup = account.Value<Boolean>("isGroup")
                                where enabled && !isGroup
                                select email).ToList();

                if (accounts.Any())
                {
                    var mergedHistory = history.GetMerged();
                    if (mergedHistory != null && mergedHistory.Events != null)
                    {
                        var eventObj = mergedHistory.Events.FirstOrDefault();
                        if (eventObj != null && eventObj.Organizer != null)
                        {
                            var organizerEmail = eventObj.Organizer.Value.ToString()
                                                         .ToLowerInvariant()
                                                         .Replace("mailto:", "");

                            canNotify = accounts.Contains(organizerEmail);
                        }
                    }
                }
            }

            return canNotify;
        }
        private string GetRRuleString(Ical.Net.CalendarComponents.CalendarEvent evt)
        {
            var rrule = string.Empty;

            if (evt.RecurrenceRules != null && evt.RecurrenceRules.Any())
            {
                var recurrenceRules = evt.RecurrenceRules.ToList();

                rrule = DDayICalParser.SerializeRecurrencePattern(recurrenceRules.First());

                if (evt.ExceptionDates != null && evt.ExceptionDates.Any())
                {
                    rrule += ";exdates=";

                    var exceptionDates = evt.ExceptionDates.ToList();

                    foreach (var periodList in exceptionDates)
                    {
                        var date = periodList.ToString();

                        //has time
                        if (date.ToLowerInvariant().IndexOf('t') >= 0)
                        {
                            //is utc time
                            if (date.ToLowerInvariant().IndexOf('z') >= 0)
                            {
                                rrule += date;
                            }
                            else
                            {
                                //convert to utc time
                                DateTime dt;
                                if (DateTime.TryParseExact(date.ToUpper(), "yyyyMMdd'T'HHmmssK", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out dt))
                                {
                                    var tzid = periodList.TzId ?? evt.Start.TzId;
                                    if (!String.IsNullOrEmpty(tzid))
                                    {
                                        dt = TimeZoneInfo.ConvertTime(dt, TimeZoneConverter.GetTimeZone(tzid), TimeZoneInfo.Utc);
                                    }
                                    rrule += dt.ToString("yyyyMMdd'T'HHmmssK");
                                }
                                else
                                {
                                    rrule += date;
                                }
                            }
                        }
                        //for yyyyMMdd/P1D date. Bug in the ical.net
                        else if (date.ToLowerInvariant().IndexOf("/p") >= 0)
                        {
                            try
                            {
                                rrule += date.Split('/')[0];
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(String.Format("Error: {0}, Date string: {1}", ex, date));
                                rrule += date;
                            }
                        }
                        else
                        {
                            rrule += date;
                        }

                        rrule += ",";
                    }

                    rrule = rrule.TrimEnd(',');
                }
            }

            return rrule;
        }
        private void CheckPermissions(ISecurityObject securityObj, Common.Security.Authorizing.Action action)
        {
            CheckPermissions(securityObj, action, false);
        }
        private bool CheckPermissions(ISecurityObject securityObj, Common.Security.Authorizing.Action action, bool silent)
        {
            if (securityObj == null)
                throw new Exception(Resources.CalendarApiResource.ErrorItemNotFound);

            if (silent)
                return SecurityContext.CheckPermissions(securityObj, action);

            SecurityContext.DemandPermissions(securityObj, action);

            return true;
        }

        /// <summary>
        /// Returns the sharing access parameters to the calendar with the ID specified in the request.
        /// </summary>
        /// <short>
        /// Get access parameters
        /// </short>
        /// <param name="calendarId">Calendar ID</param>
        /// <category>Calendars and subscriptions</category>
        /// <returns>Sharing access parameters</returns>
        [Read("{calendarId}/sharing")]
        public PublicItemCollection GetCalendarSharingOptions(int calendarId)
        {
            var cal = _dataProvider.GetCalendarById(calendarId);
            if (cal == null)
                throw new Exception(Resources.CalendarApiResource.ErrorItemNotFound);

            return PublicItemCollection.GetForCalendar(cal);
        }

        /// <summary>
        /// Returns the default values for the sharing access parameters.
        /// </summary>
        /// <short>
        /// Get default access parameters
        /// </short>
        /// <category>Calendars and subscriptions</category>
        /// <returns>Default sharing access parameters</returns>
        [Read("sharing")]
        public PublicItemCollection GetDefaultSharingOptions()
        {
            return PublicItemCollection.GetDefault();
        }

        private static string GetUserAuthorization(string email)
        {
            var user = CoreContext.UserManager.GetUserByEmail(email);
            if (user == null || !CheckUserEmail(user)) return string.Empty;
            email = user.Email.ToLower();
            var currentAccountPaswd = InstanceCrypto.Encrypt(email);
            return email + ":" + currentAccountPaswd;
        }

        public void Dispose()
        {
            if (_dataProvider != null)
            {
                _dataProvider.Dispose();
            }
        }

        private static bool CheckUserEmail(ASC.Core.Users.UserInfo user)
        {
            //CoreContext.UserManager.IsSystemUser(user.ID)
            if (string.IsNullOrEmpty(user.Email))
            {
                Logger.DebugFormat("CalendarApi: user {0} has no email. {1}", user.ID, Environment.StackTrace);
                return false;
            }

            return true;
        }
    }
}
