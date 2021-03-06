﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Management.Instrumentation;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TicketDesk.PushNotifications.Common.Model;

namespace TicketDesk.PushNotifications.Common
{
    public static class PushNotificationDeliveryManager
    {
        private static ICollection<IPushNotificationDeliveryProvider> _deliveryProviders;

        public static ICollection<IPushNotificationDeliveryProvider> DeliveryProviders
        {
            get
            {
                if (_deliveryProviders == null)
                {
                    _deliveryProviders = new List<IPushNotificationDeliveryProvider>();
                    using (var context = new TdPushNotificationContext())
                    {
                        var providerConfigs = context.TicketDeskPushNotificationSettings.DeliveryProviderSettings;
                        foreach (var prov in providerConfigs)
                        {
                            if (prov.IsEnabled)
                            {
                                _deliveryProviders.Add(CreateDeliveryProviderInstance(prov));
                            }
                        }
                    }
                }
                return _deliveryProviders;
            }
            set { _deliveryProviders = value; }
        }

        public static IPushNotificationDeliveryProvider CreateDeliveryProviderInstance(ApplicationPushNotificationSetting.PushNotificationDeliveryProviderSetting settings)
        {
            var provType = Type.GetType(settings.ProviderAssemblyQualifiedName);
            if (provType != null)
            {
                var ci = provType.GetConstructor(new[] {typeof (JObject)});
                if (ci != null)
                {
                   return (IPushNotificationDeliveryProvider) ci.Invoke(new[] {settings.ProviderConfigurationData});
                }
            }
            return null;
        }

        public static IPushNotificationDeliveryProvider CreateDefaultDeliveryProviderInstance(Type providerType)
        {
            var ci = providerType.GetConstructor(new[] { typeof(JObject) });
            if (ci == null)
            {
                throw new InstanceNotFoundException("Cannot locate a constructor for " + typeof(JObject).Name);
            }
            return (IPushNotificationDeliveryProvider)ci.Invoke(new object[] { null });
        }


        public static async Task SendNotification
        (
            int contentSourceId,
            string contentSourceType,
            string subscriberId,
            int destinationId
        )
        {
            using (var context = new TdPushNotificationContext())
            {
                
                var readyNote = await context.PushNotificationItems
                    .FirstOrDefaultAsync(n =>
                        (
                            n.ContentSourceId == contentSourceId &&
                            n.ContentSourceType == contentSourceType &&
                            n.SubscriberId == subscriberId &&
                            n.DestinationId == destinationId
                        ) &&
                        (
                            n.DeliveryStatus == PushNotificationItemStatus.Scheduled ||
                            n.DeliveryStatus == PushNotificationItemStatus.Retrying)
                        );
                if (readyNote == null) { return; }

                await SendNotificationMessageAsync(context, readyNote);
                await context.SaveChangesAsync();
            }
        }



        public static async Task SendNextReadyNotification()
        {
            using (var context = new TdPushNotificationContext())
            {
                //get the next notification that is ready to send
                var readyNote =
                    await context.PushNotificationItems.OrderBy(n => n.ScheduledSendDate).FirstOrDefaultAsync(
                        n =>
                            (n.DeliveryStatus == PushNotificationItemStatus.Scheduled || n.DeliveryStatus == PushNotificationItemStatus.Retrying) &&
                            n.ScheduledSendDate <= DateTimeOffset.Now);


                await SendNotificationMessageAsync(context, readyNote);
                await context.SaveChangesAsync();
            }
        }


        private static async Task SendNotificationMessageAsync(TdPushNotificationContext context, PushNotificationItem readyNote)
        {
            var retryMax = context.TicketDeskPushNotificationSettings.RetryAttempts;
            var retryIntv = context.TicketDeskPushNotificationSettings.RetryIntervalMinutes;

            //find a provider for the notification destination type
            var provider = DeliveryProviders.FirstOrDefault(p => p.DestinationType == readyNote.Destination.DestinationType);
            if (provider == null)
            {
                //no provider
                readyNote.DeliveryStatus = PushNotificationItemStatus.NotAvailable;
                readyNote.ScheduledSendDate = null;
            }
            else
            {
                await provider.SendReadyMessageAsync(readyNote, retryMax, retryIntv);
            }
        }

        /*
        InProcess
         *  Create context
         *  Get one ready item
         *  Mark sending??/
         *  Generate email
         *  Send email
         *      fail
         *         Mark for retry
         *      success     
         *          Mark sent
         *  Clear context
         *      If two or more failues, break;
         *      Repeat until all are sent
        Azure
         *  WebJob Schedule Trigger
         *      Create context
         *      Get all ready items
         *      Queue all ready items
         *      Mark sending??
         *      Clear context - repeat until all are queued
         *  WebJob Queue Trigger Function
         *      Create context
         *      Check if sent
         *          Discard if sent
         *      Generate email
         *      Send email
         *          fail
         *              Mark for retry
         *          success     
         *              Mark sent
         *      
        */

    }
}
