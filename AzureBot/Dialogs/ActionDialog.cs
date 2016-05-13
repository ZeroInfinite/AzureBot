﻿namespace AzureBot.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using Azure.Management.ResourceManagement;
    using FormTemplates;
    using Microsoft.Bot.Builder.Dialogs;
    using Microsoft.Bot.Builder.FormFlow;
    using Microsoft.Bot.Builder.Luis;
    using Microsoft.Bot.Builder.Luis.Models;
    using Microsoft.Bot.Connector;

    [LuisModel("c9e598cb-0e5f-48f6-b14a-ebbb390a6fb3", "a7c1c493d0e244e796b83c6785c4be4d")]
    [Serializable]
    public class ActionDialog : LuisDialog<string>
    {
        private static string[] ordinals = { "first", "second", "third", "fourth", "fifth" };

        private readonly string originalMessage;

        private readonly ILuisService luisService;

        public ActionDialog(string originalMessage)
        {
            this.originalMessage = originalMessage;

            if (this.luisService == null)
            {
                var type = this.GetType();
                var luisModel = type.GetCustomAttribute<LuisModelAttribute>(inherit: true);
                if (luisModel == null)
                {
                    throw new Exception("Luis model attribute is not set for the class");
                }

                this.luisService = new LuisService(luisModel);
            }

            this.handlerByIntent = new Dictionary<string, IntentHandler>(this.GetHandlersByIntent());
        }

        public override async Task StartAsync(IDialogContext context)
        {
            var luisResult = await this.luisService.QueryAsync(this.originalMessage);

            var intent = luisResult.Intents.OrderByDescending(i => i.Score).FirstOrDefault();

            IntentHandler intentHandler;

            if (intent != null &&
                this.handlerByIntent.TryGetValue(intent.Intent, out intentHandler))
            {
                await intentHandler(context, luisResult);
            }
            else
            {
                await this.None(context, luisResult);
            }
        }

        [LuisIntent("")]
        public async Task None(IDialogContext context, LuisResult result)
        {
            string message = $"Sorry I did not understand: " + string.Join(", ", result.Intents.Select(i => i.Intent));

            await context.PostAsync(message);

            context.Wait(this.MessageReceived);
        }

        [LuisIntent("ListSubscriptions")]
        public async Task ListSubscriptionsAsync(IDialogContext context, LuisResult result)
        {
            int index = 0;
            var subscriptions = await new AzureRepository().ListSubscriptionsAsync();

            var subscriptionsText = subscriptions.Aggregate(
                string.Empty,
                (current, next) =>
                    {
                        index++;
                        return current += $"\r\n{index}. {next.DisplayName}";
                    });

            await context.PostAsync($"Your subscriptions are: {subscriptionsText}");

            context.Wait(this.MessageReceived);
        }

        [LuisIntent("UseSubscription")]
        public async Task UseSubscriptionAsync(IDialogContext context, LuisResult result)
        {
            var subscriptions = await new AzureRepository().ListSubscriptionsAsync();

            var entity = result.Entities.OrderByDescending(p => p.Score).FirstOrDefault();
            if (entity != null)
            {
                var subscriptionName = entity.Entity;
                if (entity.Type == "builtin.ordinal")
                {
                    var ordinal = Array.IndexOf(ordinals, entity.Entity.ToLowerInvariant());
                    if (ordinal >= 0)
                    {
                        subscriptionName = subscriptions.ElementAt(ordinal).DisplayName;
                    }
                }

                await context.PostAsync($"Using the {subscriptionName} subscription.");
            }
            else
            {
                await context.PostAsync("Which subscription do you want to use?");
            }

            context.Wait(this.MessageReceived);
        }

        [LuisIntent("ListVms")]
        public async Task ListVmsAsync(IDialogContext context, LuisResult result)
        {
            var subscriptionId = context.PerUserInConversationData.Get<string>(ContextConstants.SubscriptionIdKey);

            var virtualMachines = await new AzureRepository().ListVirtualMachinesAsync(subscriptionId);

            int index = 0;
            var virtualMachinesText = virtualMachines.Aggregate(
                string.Empty,
                (current, next) =>
                    {
                        index++;
                        return current += $"\r\n{index}. {next.Name}";
                    });

            await context.PostAsync($"Available VMs are: {virtualMachinesText}");
            context.Wait(this.MessageReceived);
        }

        [LuisIntent("StartVm")]
        public async Task StartVmAsync(IDialogContext context, LuisResult result)
        {
            // retrieve available VM names from the current subscription
            var subscriptionId = context.PerUserInConversationData.Get<string>(ContextConstants.SubscriptionIdKey);
            var availableVMs = (await new AzureRepository().ListVirtualMachinesAsync(subscriptionId))
                                .Select(p => p.Name)
                                .ToArray();

            var form = new FormDialog<VirtualMachineFormState>(
                new VirtualMachineFormState(availableVMs, Operations.Start),
                EntityForms.BuildVirtualMachinesForm,
                FormOptions.PromptInStart,
                result.Entities);
            context.Call(form, this.StartVirtualMachineFormComplete);
        }

        [LuisIntent("StopVm")]
        public async Task StopVmAsync(IDialogContext context, LuisResult result)
        {
            var subscriptionId = context.PerUserInConversationData.Get<string>(ContextConstants.SubscriptionIdKey);
            var availableVMs = (await new AzureRepository().ListVirtualMachinesAsync(subscriptionId))
                               .Select(p => p.Name)
                               .ToArray();

            var form = new FormDialog<VirtualMachineFormState>(
                new VirtualMachineFormState(availableVMs, Operations.Stop),
                EntityForms.BuildVirtualMachinesForm,
                FormOptions.PromptInStart,
                result.Entities);
            context.Call(form, this.StopVirtualMachineFormComplete);
        }

        [LuisIntent("RunRunbook")]
        public async Task RunRunbookAsync(IDialogContext context, LuisResult result)
        {
            var entity = result.Entities.OrderByDescending(p => p.Score).FirstOrDefault();
            if (entity != null)
            {
                var runbookName = entity.Entity;
                await context.PostAsync($"Launching the {runbookName} runbook.");
            }
            else
            {
                await context.PostAsync("Which runbook do you want to run?");
            }

            context.Wait(this.MessageReceived);
        }

        private async Task StartVirtualMachineFormComplete(IDialogContext context, IAwaitable<VirtualMachineFormState> result)
        {
            try
            {
                var virtualMachineFormState = await result;
                var subscriptionId = context.PerUserInConversationData.Get<string>(ContextConstants.SubscriptionIdKey);
                await context.PostAsync($"Starting the {virtualMachineFormState.VirtualMachine} virtual machine.");
                await new AzureRepository().StartVirtualMachineAsync(subscriptionId, virtualMachineFormState.VirtualMachine);
            }
            catch (FormCanceledException<VirtualMachineFormState>)
            {
                await context.PostAsync("You have canceled the operation. What would you like to do next?");
            }

            context.Wait(this.MessageReceived);
        }

        private async Task StopVirtualMachineFormComplete(IDialogContext context, IAwaitable<VirtualMachineFormState> result)
        {
            try
            {
                var virtualMachineFormState = await result;
                var subscriptionId = context.PerUserInConversationData.Get<string>(ContextConstants.SubscriptionIdKey);
                await context.PostAsync($"Stopping the {virtualMachineFormState.VirtualMachine} virtual machine.");
                await new AzureRepository().StartVirtualMachineAsync(subscriptionId, virtualMachineFormState.VirtualMachine);
            }
            catch (FormCanceledException<VirtualMachineFormState>)
            {
                await context.PostAsync("You have canceled the operation. What would you like to do next?");
            }

            context.Wait(this.MessageReceived);
        }
    }
}