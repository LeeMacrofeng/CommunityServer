/*
 *
 * (c) Copyright Ascensio System Limited 2010-2018
 *
 * This program is freeware. You can redistribute it and/or modify it under the terms of the GNU 
 * General Public License (GPL) version 3 as published by the Free Software Foundation (https://www.gnu.org/copyleft/gpl.html). 
 * In accordance with Section 7(a) of the GNU GPL its Section 15 shall be amended to the effect that 
 * Ascensio System SIA expressly excludes the warranty of non-infringement of any third-party rights.
 *
 * THIS PROGRAM IS DISTRIBUTED WITHOUT ANY WARRANTY; WITHOUT EVEN THE IMPLIED WARRANTY OF MERCHANTABILITY OR
 * FITNESS FOR A PARTICULAR PURPOSE. For more details, see GNU GPL at https://www.gnu.org/copyleft/gpl.html
 *
 * You can contact Ascensio System SIA by email at sales@onlyoffice.com
 *
 * The interactive user interfaces in modified source and object code versions of ONLYOFFICE must display 
 * Appropriate Legal Notices, as required under Section 5 of the GNU GPL version 3.
 *
 * Pursuant to Section 7 § 3(b) of the GNU GPL you must retain the original ONLYOFFICE logo which contains 
 * relevant author attributions when distributing the software. If the display of the logo in its graphic 
 * form is not reasonably feasible for technical reasons, you must include the words "Powered by ONLYOFFICE" 
 * in every copy of the program you distribute. 
 * Pursuant to Section 7 § 3(e) we decline to grant you any rights under trademark law for use of our trademarks.
 *
*/


using System;
using System.Security.Authentication;
using System.Threading;
using System.Web;
using ASC.ActiveDirectory;
using ASC.ActiveDirectory.ComplexOperations;
using ASC.Api.Attributes;
using ASC.Api.Interfaces;
using ASC.Api.Utils;
using ASC.Common.Data;
using ASC.Common.Data.Sql;
using ASC.Core;
using ASC.Core.Common.Settings;
using ASC.Core.Users;
using ASC.FederatedLogin.LoginProviders;
using ASC.IPSecurity;
using ASC.MessagingSystem;
using ASC.Security.Cryptography;
using ASC.Web.Studio.Core;
using ASC.Web.Studio.Core.Import;
using ASC.Web.Studio.Core.Notify;
using ASC.Web.Studio.Core.SMS;
using ASC.Web.Studio.Core.Users;
using ASC.Web.Studio.UserControls.Common;
using Resources;

namespace ASC.Specific.AuthorizationApi
{
    /// <summary>
    /// Authorization for api
    /// </summary>
    public class AuthenticationEntryPoint : IApiEntryPoint
    {
        /// <summary>
        /// Entry point name
        /// </summary>
        public string Name
        {
            get { return "authentication"; }
        }

        private static HttpRequest Request
        {
            get { return HttpContext.Current.Request; }
        }

        /// <summary>
        /// Gets authentication token for use in api authorization
        /// </summary>
        /// <short>
        /// Get token
        /// </short>
        /// <param name="userName">user name or email</param>
        /// <param name="password">password</param>
        /// <param name="provider">social media provider type</param>
        /// <param name="accessToken">provider token</param>
        /// <returns>tokent to use in 'Authorization' header when calling API methods</returns>
        /// <exception cref="AuthenticationException">Thrown when not authenticated</exception>
        [Create(@"", false, false)] //NOTE: this method doesn't requires auth!!!  //NOTE: this method doesn't check payment!!!
        public AuthenticationTokenData AuthenticateMe(string userName, string password, string provider, string accessToken)
        {
            bool viaEmail;
            var user = GetUser(userName, password, provider, accessToken, out viaEmail);

            if (!StudioSmsNotificationSettings.IsVisibleSettings || !StudioSmsNotificationSettings.Enable)
            {
                try
                {
                    var token = SecurityContext.AuthenticateMe(user.ID);

                    MessageService.Send(Request, viaEmail ? MessageAction.LoginSuccessViaApi : MessageAction.LoginSuccessViaApiSocialAccount);

                    return new AuthenticationTokenData
                        {
                            Token = token,
                            Expires = new ApiDateTime(DateTime.UtcNow.AddYears(1))
                        };
                }
                catch
                {
                    MessageService.Send(Request, user.DisplayUserName(false), viaEmail ? MessageAction.LoginFailViaApi : MessageAction.LoginFailViaApiSocialAccount);
                    throw new AuthenticationException("User authentication failed");
                }
                finally
                {
                    SecurityContext.Logout();
                }
            }


            if (string.IsNullOrEmpty(user.MobilePhone) || user.MobilePhoneActivationStatus == MobilePhoneActivationStatus.NotActivated)
                return new AuthenticationTokenData
                    {
                        Sms = true
                    };

            SmsManager.PutAuthCode(user, false);

            return new AuthenticationTokenData
                {
                    Sms = true,
                    PhoneNoise = SmsManager.BuildPhoneNoise(user.MobilePhone),
                    Expires = new ApiDateTime(DateTime.UtcNow.Add(SmsKeyStorage.TrustInterval))
                };
        }

        /// <summary>
        /// Set mobile phone for user
        /// </summary>
        /// <param name="userName">user name or email</param>
        /// <param name="password">password</param>
        /// <param name="provider">social media provider type</param>
        /// <param name="accessToken">provider token</param>
        /// <param name="mobilePhone">new mobile phone</param>
        /// <returns>mobile phone</returns>
        [Create(@"setphone", false, false)] //NOTE: this method doesn't requires auth!!!  //NOTE: this method doesn't check payment!!!
        public AuthenticationTokenData SaveMobilePhone(string userName, string password, string provider, string accessToken, string mobilePhone)
        {
            bool viaEmail;
            var user = GetUser(userName, password, provider, accessToken, out viaEmail);
            mobilePhone = SmsManager.SaveMobilePhone(user, mobilePhone);
            MessageService.Send(HttpContext.Current.Request, MessageAction.UserUpdatedMobileNumber, MessageTarget.Create(user.ID), user.DisplayUserName(false), mobilePhone);

            return new AuthenticationTokenData
                {
                    Sms = true,
                    PhoneNoise = SmsManager.BuildPhoneNoise(mobilePhone),
                    Expires = new ApiDateTime(DateTime.UtcNow.Add(SmsKeyStorage.TrustInterval))
                };
        }

        /// <summary>
        /// Send sms code again
        /// </summary>
        /// <param name="userName">user name or email</param>
        /// <param name="password">password</param>
        /// <param name="provider">social media provider type</param>
        /// <param name="accessToken">provider token</param>
        /// <returns>mobile phone</returns>
        [Create(@"sendsms", false, false)] //NOTE: this method doesn't requires auth!!!  //NOTE: this method doesn't check payment!!!
        public AuthenticationTokenData SendSmsCode(string userName, string password, string provider, string accessToken)
        {
            bool viaEmail;
            var user = GetUser(userName, password, provider, accessToken, out viaEmail);
            SmsManager.PutAuthCode(user, true);

            return new AuthenticationTokenData
                {
                    Sms = true,
                    PhoneNoise = SmsManager.BuildPhoneNoise(user.MobilePhone),
                    Expires = new ApiDateTime(DateTime.UtcNow.Add(SmsKeyStorage.TrustInterval))
                };
        }

        /// <summary>
        /// Gets two-factor authentication token for use in api authorization
        /// </summary>
        /// <short>
        /// Get token
        /// </short>
        /// <param name="userName">user name or email</param>
        /// <param name="password">password</param>
        /// <param name="provider">social media provider type</param>
        /// <param name="accessToken">provider token</param>
        /// <param name="code">sms code</param>
        /// <returns>tokent to use in 'Authorization' header when calling API methods</returns>
        [Create(@"{code}", false, false)] //NOTE: this method doesn't requires auth!!!  //NOTE: this method doesn't check payment!!!
        public AuthenticationTokenData AuthenticateMe(string userName, string password, string provider, string accessToken, string code)
        {
            bool viaEmail;
            var user = GetUser(userName, password, provider, accessToken, out viaEmail);

            try
            {
                SmsManager.ValidateSmsCode(user, code);

                var token = SecurityContext.AuthenticateMe(user.ID);

                MessageService.Send(Request, MessageAction.LoginSuccessViaApiSms);

                return new AuthenticationTokenData
                    {
                        Token = token,
                        Expires = new ApiDateTime(DateTime.UtcNow.AddYears(1)),
                        Sms = true,
                        PhoneNoise = SmsManager.BuildPhoneNoise(user.MobilePhone)
                    };
            }
            catch
            {
                MessageService.Send(Request, user.DisplayUserName(false), MessageAction.LoginFailViaApiSms, MessageTarget.Create(user.ID));
                throw new AuthenticationException("User authentication failed");
            }
            finally
            {
                SecurityContext.Logout();
            }
        }

        /// <summary>
        /// Request of invitation by email on personal.onlyoffice.com
        /// </summary>
        /// <param name="email">Email address</param>
        /// <param name="lang">Culture</param>
        /// <param name="spam">User consent to subscribe to the ONLYOFFICE newsletter</param>
        /// <visible>false</visible>
        [Create(@"register", false)] //NOTE: this method doesn't requires auth!!!
        public string RegisterUserOnPersonal(string email, string lang, bool spam)
        {
            if (!CoreContext.Configuration.Personal) throw new MethodAccessException("Method is only available on personal.onlyoffice.com");

            try
            {
                var cultureInfo = SetupInfo.EnabledCultures.Find(c => String.Equals(c.TwoLetterISOLanguageName, lang, StringComparison.InvariantCultureIgnoreCase));
                if (cultureInfo != null)
                {
                    Thread.CurrentThread.CurrentUICulture = cultureInfo;
                }

                email.ThrowIfNull(new ArgumentException(Resource.ErrorEmailEmpty, "email"));

                if (!email.TestEmailRegex()) throw new ArgumentException(Resource.ErrorNotCorrectEmail, "email");

                var newUserInfo = CoreContext.UserManager.GetUserByEmail(email);

                if (CoreContext.UserManager.UserExists(newUserInfo.ID))
                {
                    if (!SetupInfo.IsSecretEmail(email) || SecurityContext.IsAuthenticated)
                    {
                        throw new Exception(CustomNamingPeople.Substitute<Resource>("ErrorEmailAlreadyExists"));
                    }

                    try
                    {
                        SecurityContext.AuthenticateMe(Core.Configuration.Constants.CoreSystem);
                        CoreContext.UserManager.DeleteUser(newUserInfo.ID);
                    }
                    finally
                    {
                        SecurityContext.Logout();
                    }
                }
                if (!spam)
                {
                    try
                    {
                        const string _databaseID = "com";
                        using (var db = DbManager.FromHttpContext(_databaseID))
                        {
                            db.ExecuteNonQuery(new SqlInsert("template_unsubscribe", false)
                                    .InColumnValue("email", email.ToLowerInvariant())
                                    .InColumnValue("reason", "personal")
                                );
                            log4net.LogManager.GetLogger("ASC.Web").Debug(String.Format("Write to template_unsubscribe {0}",email.ToLowerInvariant()));
                        }
                    }
                    catch (Exception ex)
                    {
                        log4net.LogManager.GetLogger("ASC.Web").Debug(String.Format("ERROR write to template_unsubscribe {0}, email:{1}", ex.Message, email.ToLowerInvariant()));
                    }
                    
                }
                StudioNotifyService.Instance.SendInvitePersonal(email);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            return string.Empty;
        }

        private static UserInfo GetUser(string userName, string password, string provider, string accessToken, out bool viaEmail)
        {
            viaEmail = true;
            var action = MessageAction.LoginFailViaApi;
            UserInfo user;
            try
            {
                if (string.IsNullOrEmpty(provider) || provider == "email")
                {
                    userName.ThrowIfNull(new ArgumentException(@"userName empty", "userName"));
                    password.ThrowIfNull(new ArgumentException(@"password empty", "password"));


                    var localization = new LdapLocalization(Resource.ResourceManager);
                    var ldapUserManager = new LdapUserManager(localization);

                    if (!ldapUserManager.TryGetAndSyncLdapUserInfo(userName, password, out user))
                    {
                        user = CoreContext.UserManager.GetUsers(
                            CoreContext.TenantManager.GetCurrentTenant().TenantId,
                            userName,
                            Hasher.Base64Hash(password, HashAlg.SHA256));
                    }

                    if (user == null || !CoreContext.UserManager.UserExists(user.ID))
                    {
                        throw new Exception("user not found");
                    }
                }
                else
                {

                    viaEmail = false;

                    action = MessageAction.LoginFailViaApiSocialAccount;
                    var thirdPartyProfile = ProviderManager.GetLoginProfile(provider, accessToken);
                    userName = thirdPartyProfile.EMail;

                    user = LoginWithThirdParty.GetUserByThirdParty(thirdPartyProfile);
                }
            }
            catch
            {
                MessageService.Send(Request, string.IsNullOrEmpty(userName) ? userName : AuditResource.EmailNotSpecified, action);
                throw new AuthenticationException("User authentication failed");
            }

            var tenant = CoreContext.TenantManager.GetCurrentTenant();
            var settings = IPRestrictionsSettings.Load();
            if (settings.Enable && user.ID != tenant.OwnerId && !IPSecurity.IPSecurity.Verify(tenant))
            {
                throw new IPSecurityException();
            }

            return user;
        }
    }
}