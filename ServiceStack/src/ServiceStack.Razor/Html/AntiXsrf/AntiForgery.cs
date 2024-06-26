﻿#if NETFRAMEWORK
// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using ServiceStack.Html.Claims;

namespace ServiceStack.Html.AntiXsrf
{
    /// <summary>
    /// Provides access to the anti-forgery system, which provides protection against
    /// Cross-site Request Forgery (XSRF, also called CSRF) attacks.
    /// </summary>
    public static class AntiForgery
    {
        private static readonly AntiForgeryWorker _worker = CreateSingletonAntiForgeryWorker();

        private static AntiForgeryWorker CreateSingletonAntiForgeryWorker()
        {
            // initialize the dependency chain

            IAntiForgeryConfig config = new AntiForgeryConfigWrapper();
            IAntiForgeryTokenSerializer serializer = new AntiForgeryTokenSerializer(MachineKey45CryptoSystem.Instance);
            ITokenStore tokenStore = new AntiForgeryTokenStore(config, serializer);
            IClaimUidExtractor claimUidExtractor = new ClaimUidExtractor(config, ClaimsIdentityConverter.Default);
            ITokenValidator tokenValidator = new TokenValidator(config, claimUidExtractor);

            return new AntiForgeryWorker(serializer, config, tokenStore, tokenValidator);
        }

        /// <summary>
        /// Generates an anti-forgery token for this request. This token can
        /// be validated by calling the Validate() method.
        /// </summary>
        /// <returns>An HTML string corresponding to an &lt;input type="hidden"&gt;
        /// element. This element should be put inside a &lt;form&gt;.</returns>
        /// <remarks>
        /// This method has a side effect: it may set a response cookie.
        /// </remarks>
        public static MvcHtmlString GetHtml()
        {
            if (HttpContext.Current == null) {
                throw new ArgumentException(MvcResources.HttpContextUnavailable);
            }

            TagBuilder retVal = _worker.GetFormInputElement(new HttpContextWrapper(HttpContext.Current));
            return retVal.ToHtmlString(TagRenderMode.SelfClosing);
        }

        /// <summary>
        /// Generates an anti-forgery token pair (cookie and form token) for this request.
        /// This method is similar to GetHtml(), but this method gives the caller control
        /// over how to persist the returned values. To validate these tokens, call the
        /// appropriate overload of Validate.
        /// </summary>
        /// <param name="oldCookieToken">The anti-forgery token - if any - that already existed
        /// for this request. May be null. The anti-forgery system will try to reuse this cookie
        /// value when generating a matching form token.</param>
        /// <param name="newCookieToken">Will contain a new cookie value if the old cookie token
        /// was null or invalid. If this value is non-null when the method completes, the caller
        /// must persist this value in the form of a response cookie, and the existing cookie value
        /// should be discarded. If this value is null when the method completes, the existing
        /// cookie value was valid and needn't be modified.</param>
        /// <param name="formToken">The value that should be stored in the &lt;form&gt;. The caller
        /// should take care not to accidentally swap the cookie and form tokens.</param>
        /// <remarks>
        /// Unlike the GetHtml() method, this method has no side effect. The caller
        /// is responsible for setting the response cookie and injecting the returned
        /// form token as appropriate.
        /// </remarks>
        public static void GetTokens(string oldCookieToken, out string newCookieToken, out string formToken)
        {
            if (HttpContext.Current == null) {
                throw new ArgumentException(MvcResources.HttpContextUnavailable);
            }

            _worker.GetTokens(new HttpContextWrapper(HttpContext.Current), oldCookieToken, out newCookieToken, out formToken);
        }

        /// <summary>
        /// Validates an anti-forgery token that was supplied for this request.
        /// The anti-forgery token may be generated by calling GetHtml().
        /// </summary>
        /// <remarks>
        /// Throws an HttpAntiForgeryException if validation fails.
        /// </remarks>
        public static void Validate()
        {
            if (HttpContext.Current == null) {
                throw new ArgumentException(MvcResources.HttpContextUnavailable);
            }

            _worker.Validate(new HttpContextWrapper(HttpContext.Current));
        }

        /// <summary>
        /// Validates an anti-forgery token pair that was generated by the GetTokens method.
        /// </summary>
        /// <param name="cookieToken">The token that was supplied in the request cookie.</param>
        /// <param name="formToken">The token that was supplied in the request form body.</param>
        /// <remarks>
        /// Throws an HttpAntiForgeryException if validation fails.
        /// </remarks>
        public static void Validate(string cookieToken, string formToken)
        {
            if (HttpContext.Current == null) {
                throw new ArgumentException(MvcResources.HttpContextUnavailable);
            }

            _worker.Validate(new HttpContextWrapper(HttpContext.Current), cookieToken, formToken);
        }
    }
}

#endif
