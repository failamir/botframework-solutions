﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmailSkill.Adapters;
using EmailSkill.Extensions;
using EmailSkill.Models;
using EmailSkill.Responses.Shared;
using EmailSkill.Responses.ShowEmail;
using EmailSkill.Utilities;
using Luis;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Skills;
using Microsoft.Bot.Builder.Skills.Models;
using Microsoft.Bot.Builder.Solutions;
using Microsoft.Bot.Builder.Solutions.Resources;
using Microsoft.Bot.Builder.Solutions.Responses;
using Microsoft.Bot.Builder.Solutions.Util;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;

namespace EmailSkill.Dialogs
{
    public class ShowEmailDialog : EmailSkillDialogBase
    {
        public ShowEmailDialog(
            IServiceProvider serviceProvider,
            IBotTelemetryClient telemetryClient)
            : base(nameof(ShowEmailDialog), serviceProvider, telemetryClient)
        {
            TelemetryClient = telemetryClient;

            var showEmail = new WaterfallStep[]
            {
                IfClearContextStep,
                GetAuthToken,
                AfterGetAuthToken,
                Display
            };

            var readEmail = new WaterfallStep[]
            {
                ReadEmail,
                Reshow
            };

            var deleteEmail = new WaterfallStep[]
            {
                DeleteEmail,
                Reshow
            };

            var forwardEmail = new WaterfallStep[]
            {
                ForwardEmail,
                Reshow
            };

            var replyEmail = new WaterfallStep[]
            {
                ReplyEmail,
                Reshow
            };

            var displayEmail = new WaterfallStep[]
            {
                IfClearPagingConditionStep,
                PagingStep,
                ShowEmails,
                PromptToHandle,
                CheckRead,
                HandleMore
            };

            var displayFilteredEmail = new WaterfallStep[]
            {
                ShowFilteredEmails,
                PromptToHandle,
                CheckRead,
                HandleMore
            };

            var redisplayEmail = new WaterfallStep[]
            {
                PromptToReshow,
                CheckReshow,
                HandleMore,
            };

            var retryUnknown = new WaterfallStep[]
            {
                SendFallback,
                RetryInput,
                HandleMore,
            };

            var forwardEmailDialog = serviceProvider.GetService<ForwardEmailDialog>();
            var replyEmailDialog = serviceProvider.GetService<ReplyEmailDialog>();
            var deleteEmailDialog = serviceProvider.GetService<DeleteEmailDialog>();

            // Define the conversation flow using a waterfall model.
            AddDialog(new WaterfallDialog(Actions.Show, showEmail) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.Read, readEmail) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.Delete, deleteEmail) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.Forward, forwardEmail) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.Reply, replyEmail) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.Display, displayEmail) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.DisplayFiltered, displayFilteredEmail) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.ReDisplay, redisplayEmail) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.RetryUnknown, retryUnknown) { TelemetryClient = telemetryClient });
            AddDialog(deleteEmailDialog ?? throw new ArgumentNullException(nameof(deleteEmailDialog)));
            AddDialog(replyEmailDialog ?? throw new ArgumentNullException(nameof(replyEmailDialog)));
            AddDialog(forwardEmailDialog ?? throw new ArgumentNullException(nameof(forwardEmailDialog)));
            AddDialog(new EventPrompt(Actions.FallbackEventPrompt, SkillEvents.FallbackHandledEventName, ResponseValidatorAsync));
            InitialDialogId = Actions.Show;
        }

        protected async Task<DialogTurnResult> IfClearPagingConditionStep(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await EmailStateAccessor.GetAsync(sc.Context);

                // Clear focus item
                state.UserSelectIndex = 0;

                // Clear search condition
                state.SenderName = null;
                state.SearchTexts = null;

                return await sc.NextAsync();
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> PromptToHandle(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await EmailStateAccessor.GetAsync(sc.Context);

                if (state.MessageList.Count == 1)
                {
                    return await sc.PromptAsync(Actions.Prompt, new PromptOptions { Prompt = ResponseManager.GetResponse(ShowEmailResponses.ReadOutOnlyOnePrompt) });
                }
                else
                {
                    return await sc.PromptAsync(Actions.Prompt, new PromptOptions { Prompt = ResponseManager.GetResponse(ShowEmailResponses.ReadOutPrompt) });
                }
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> PromptToReshow(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                return await sc.PromptAsync(Actions.Prompt, new PromptOptions { Prompt = ResponseManager.GetResponse(ShowEmailResponses.ReadOutMorePrompt) });
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> CheckRead(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var skillOptions = (EmailSkillDialogOptions)sc.Options;
                skillOptions.SubFlowMode = true;

                var state = await EmailStateAccessor.GetAsync(sc.Context);
                var luisResult = sc.Context.TurnState.Get<EmailLuis>(StateProperties.EmailLuisResult);

                var topIntent = luisResult?.TopIntent().intent;
                if (topIntent == null)
                {
                    return await sc.EndDialogAsync(true);
                }

                sc.Context.Activity.Properties.TryGetValue("OriginText", out var content);
                var userInput = content != null ? content.ToString() : sc.Context.Activity.Text;
                var promptRecognizerResult = ConfirmRecognizerHelper.ConfirmYesOrNo(userInput, sc.Context.Activity.Locale);
                if (promptRecognizerResult.Succeeded && promptRecognizerResult.Value == false)
                {
                    await sc.Context.SendActivityAsync(ResponseManager.GetResponse(EmailSharedResponses.CancellingMessage));
                    return await sc.EndDialogAsync(false);
                }
                else if (promptRecognizerResult.Succeeded && promptRecognizerResult.Value == true)
                {
                    return await sc.ReplaceDialogAsync(Actions.Read, skillOptions);
                }

                return await sc.NextAsync();
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> ReadEmail(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await EmailStateAccessor.GetAsync(sc.Context);
                var skillOptions = (EmailSkillDialogOptions)sc.Options;

                sc.Context.Activity.Properties.TryGetValue("OriginText", out var content);
                var userInput = content != null ? content.ToString() : sc.Context.Activity.Text;

                var luisResult = sc.Context.TurnState.Get<EmailLuis>(StateProperties.EmailLuisResult);
                var topIntent = luisResult?.TopIntent().intent;
                var generalLuisResult = sc.Context.TurnState.Get<General>(StateProperties.GeneralLuisResult);
                var generalTopIntent = generalLuisResult?.TopIntent().intent;

                if (topIntent == null)
                {
                    return await sc.EndDialogAsync(true);
                }

                await DigestFocusEmailAsync(sc);

                var message = state.Message.FirstOrDefault();
                if (message == null)
                {
                    state.Message.Add(state.MessageList[0]);
                    message = state.Message.FirstOrDefault();
                }

                var promptRecognizerResult = ConfirmRecognizerHelper.ConfirmYesOrNo(userInput, sc.Context.Activity.Locale);

                if ((topIntent == EmailLuis.Intent.None
                    || topIntent == EmailLuis.Intent.SearchMessages
                    || (topIntent == EmailLuis.Intent.ReadAloud && !IsReadMoreIntent(generalTopIntent, sc.Context.Activity.Text))
                    || (promptRecognizerResult.Succeeded && promptRecognizerResult.Value == true))
                    && message != null)
                {
                    var senderIcon = await GetUserPhotoUrlAsync(sc.Context, message.Sender.EmailAddress);
                    var emailCard = new EmailCardData
                    {
                        Subject = message.Subject,
                        Sender = message.Sender.EmailAddress.Name,
                        EmailContent = message.BodyPreview,
                        EmailLink = message.WebLink,
                        ReceivedDateTime = message?.ReceivedDateTime == null
                            ? CommonStrings.NotAvailable
                            : message.ReceivedDateTime.Value.UtcDateTime.ToDetailRelativeString(state.GetUserTimeZone()),
                        Speak = SpeakHelper.ToSpeechEmailDetailOverallString(message, state.GetUserTimeZone()),
                        SenderIcon = senderIcon
                    };

                    emailCard = await ProcessRecipientPhotoUrl(sc.Context, emailCard, message.ToRecipients);

                    var tokens = new StringDictionary()
                    {
                        { "EmailDetails", SpeakHelper.ToSpeechEmailDetailString(message, state.GetUserTimeZone()) },
                        { "EmailDetailsWithContent", SpeakHelper.ToSpeechEmailDetailString(message, state.GetUserTimeZone(), true) },
                    };

                    var recipientCard = message.ToRecipients.Count() > 5 ? GetDivergedCardName(sc.Context, "DetailCard_RecipientMoreThanFive") : GetDivergedCardName(sc.Context, "DetailCard_RecipientLessThanFive");
                    var replyMessage = ResponseManager.GetCardResponse(
                        ShowEmailResponses.ReadOutMessage,
                        new Card("EmailDetailCard", emailCard),
                        tokens,
                        "items",
                        new List<Card>().Append(new Card(recipientCard, emailCard)));

                    // Set email as read.
                    var service = ServiceManager.InitMailService(state.Token, state.GetUserTimeZone(), state.MailSourceType);
                    await service.MarkMessageAsReadAsync(message.Id);

                    await sc.Context.SendActivityAsync(replyMessage);
                }

                return await sc.NextAsync();
            }
            catch (SkillException ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> CheckReshow(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await EmailStateAccessor.GetAsync(sc.Context);
                var skillOptions = (EmailSkillDialogOptions)sc.Options;
                skillOptions.SubFlowMode = true;

                sc.Context.Activity.Properties.TryGetValue("OriginText", out var content);
                var userInput = content != null ? content.ToString() : sc.Context.Activity.Text;
                var promptRecognizerResult = ConfirmRecognizerHelper.ConfirmYesOrNo(userInput, sc.Context.Activity.Locale);
                if (promptRecognizerResult.Succeeded && promptRecognizerResult.Value == false)
                {
                    await sc.Context.SendActivityAsync(ResponseManager.GetResponse(EmailSharedResponses.CancellingMessage));
                    return await sc.EndDialogAsync(true);
                }
                else if (promptRecognizerResult.Succeeded && promptRecognizerResult.Value == true)
                {
                    return await sc.ReplaceDialogAsync(Actions.Display, skillOptions);
                }

                return await sc.NextAsync();
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> HandleMore(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await EmailStateAccessor.GetAsync(sc.Context);
                var luisResult = sc.Context.TurnState.Get<EmailLuis>(StateProperties.EmailLuisResult);
                var topIntent = luisResult?.TopIntent().intent;

                var generalIntent = sc.Context.TurnState.Get<General>(StateProperties.GeneralLuisResult);
                var topGeneralIntent = generalIntent?.TopIntent().intent;
                if (topIntent == null)
                {
                    return await sc.EndDialogAsync(true);
                }

                sc.Context.Activity.Properties.TryGetValue("OriginText", out var content);
                var userInput = content != null ? content.ToString() : sc.Context.Activity.Text;

                await DigestFocusEmailAsync(sc);

                var skillOptions = (EmailSkillDialogOptions)sc.Options;
                skillOptions.SubFlowMode = true;

                if (topIntent == EmailLuis.Intent.Delete)
                {
                    return await sc.BeginDialogAsync(Actions.Delete, skillOptions);
                }
                else if (topIntent == EmailLuis.Intent.Forward)
                {
                    return await sc.BeginDialogAsync(Actions.Forward, skillOptions);
                }
                else if (topIntent == EmailLuis.Intent.Reply)
                {
                    return await sc.BeginDialogAsync(Actions.Reply, skillOptions);
                }
                else if (IsReadMoreIntent(topGeneralIntent, userInput)
                    || (topIntent == EmailLuis.Intent.ShowNext || topIntent == EmailLuis.Intent.ShowPrevious || topGeneralIntent == General.Intent.ShowPrevious || topGeneralIntent == General.Intent.ShowNext))
                {
                    return await sc.ReplaceDialogAsync(Actions.Display, skillOptions);
                }
                else
                {
                    var cachedMessageList = state.MessageList;
                    var cachedFocusedMessages = state.Message;

                    await DigestEmailLuisResult(sc, true);
                    await SearchEmailsFromList(sc, cancellationToken);

                    if (state.MessageList.Count > 0)
                    {
                        return await sc.ReplaceDialogAsync(Actions.DisplayFiltered, skillOptions);
                    }

                    state.MessageList = cachedMessageList;
                    state.Message = cachedFocusedMessages;

                    return await sc.ReplaceDialogAsync(Actions.RetryUnknown, skillOptions);
                }
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> DeleteEmail(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var skillOptions = (EmailSkillDialogOptions)sc.Options;
                skillOptions.SubFlowMode = true;

                return await sc.BeginDialogAsync(nameof(DeleteEmailDialog), skillOptions);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> ForwardEmail(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var skillOptions = (EmailSkillDialogOptions)sc.Options;
                skillOptions.SubFlowMode = true;

                return await sc.BeginDialogAsync(nameof(ForwardEmailDialog), skillOptions);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> ReplyEmail(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var skillOptions = (EmailSkillDialogOptions)sc.Options;
                skillOptions.SubFlowMode = true;

                return await sc.BeginDialogAsync(nameof(ReplyEmailDialog), skillOptions);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> Display(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var skillOptions = (EmailSkillDialogOptions)sc.Options;
                return await sc.ReplaceDialogAsync(Actions.Display, skillOptions);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> Reshow(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var skillOptions = (EmailSkillDialogOptions)sc.Options;
                return await sc.ReplaceDialogAsync(Actions.ReDisplay, skillOptions);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> ShowFilteredEmails(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await EmailStateAccessor.GetAsync(sc.Context);

                if (state.MessageList.Count > 0)
                {
                    if (state.Message.Count == 0)
                    {
                        state.Message.Add(state.MessageList[0]);

                        if (state.MessageList.Count > 1)
                        {
                            var importCount = 0;

                            foreach (var msg in state.MessageList)
                            {
                                if (msg.Importance.HasValue && msg.Importance.Value == Importance.High)
                                {
                                    importCount++;
                                }
                            }

                            await ShowMailList(sc, state.MessageList, state.MessageList.Count(), importCount, cancellationToken);
                            return await sc.NextAsync();
                        }
                        else if (state.MessageList.Count == 1)
                        {
                            return await sc.ReplaceDialogAsync(Actions.Read, options: sc.Options);
                        }
                    }
                    else
                    {
                        return await sc.ReplaceDialogAsync(Actions.Read, options: sc.Options);
                    }

                    await sc.Context.SendActivityAsync(ResponseManager.GetResponse(EmailSharedResponses.DidntUnderstandMessage));
                    return await sc.EndDialogAsync(true);
                }
                else
                {
                    await sc.Context.SendActivityAsync(ResponseManager.GetResponse(EmailSharedResponses.EmailNotFound));
                }

                return await sc.EndDialogAsync(true);
            }
            catch (SkillException ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected Task<bool> ResponseValidatorAsync(PromptValidatorContext<Activity> pc, CancellationToken cancellationToken)
        {
            var activity = pc.Recognized.Value;
            if (activity != null && activity.Type == ActivityTypes.Event && activity.Name == SkillEvents.FallbackHandledEventName)
            {
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        protected async Task<DialogTurnResult> SendFallback(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await EmailStateAccessor.GetAsync(sc.Context);

                // Send Fallback Event
                if (sc.Context.Adapter is EmailSkillWebSocketBotAdapter remoteInvocationAdapter)
                {
                    await remoteInvocationAdapter.SendRemoteFallbackEventAsync(sc.Context, cancellationToken).ConfigureAwait(false);

                    // Wait for the FallbackHandle event
                    return await sc.PromptAsync(Actions.FallbackEventPrompt, new PromptOptions()).ConfigureAwait(false);
                }

                return await sc.NextAsync();
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> RetryInput(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await EmailStateAccessor.GetAsync(sc.Context);

                return await sc.PromptAsync(Actions.Prompt, new PromptOptions { Prompt = ResponseManager.GetResponse(EmailSharedResponses.RetryInput) });
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }
    }
}