/*
    Copyright (c) Ascensio System SIA 2022. All rights reserved.
    http://www.onlyoffice.com
*/
function setCustomVh(){var n=window.innerHeight*.01+"px";document.documentElement.style.setProperty("--vh",n)}function setContentFocus(n){var t,i;[33,34,35,36,38,40].indexOf(n.keyCode)!=-1&&(t=document.querySelector("#studioPageContent .mainPageContent"),n.target!=t)&&(n.target instanceof HTMLTextAreaElement||n.target instanceof HTMLInputElement&&(i=["text","password","number","email"],i.indexOf(n.target.type)>=0)||jq&&jq(".popupContainerClass:visible, .advanced-selector-container:visible").length||(t.setAttribute("tabindex",-1),t.focus()))}window.addEventListener("resize",setCustomVh);window.addEventListener("keydown",setContentFocus);setCustomVh();

