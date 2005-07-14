// **********************************************************************
//
// Copyright (c) 2003-2005 ZeroC, Inc. All rights reserved.
//
// This copy of Ice is licensed to you under the terms described in the
// ICE_LICENSE file included in this distribution.
//
// **********************************************************************


#include "stdafx.h"
#include "IceE/SafeStdio.h"
#include "Router.h"
#include "ChatClient.h"
#include "ChatConfigDlg.h"

#ifdef _DEBUG
#define new DEBUG_NEW
#endif

class ChatCallbackI : public Demo::ChatCallback
{
public:

    ChatCallbackI(LogIPtr log)
        : _log(log)
    {
    }

    virtual void
    message(const std::string& data, const Ice::Current&)
    {
        _log->message(data);
    }

private:

    LogIPtr _log;
};

CChatConfigDlg::CChatConfigDlg(const Ice::CommunicatorPtr& communicator, const LogIPtr& log, 
			       CChatClientDlg* mainDiag, CWnd* pParent /*=NULL*/) :
    CDialog(CChatConfigDlg::IDD, pParent), _communicator(communicator), _log(log), _mainDiag(mainDiag)
{
    _hIcon = AfxGetApp()->LoadIcon(IDR_MAINFRAME);
}

void
CChatConfigDlg::DoDataExchange(CDataExchange* pDX)
{
    CDialog::DoDataExchange(pDX);
}

BEGIN_MESSAGE_MAP(CChatConfigDlg, CDialog)
    ON_WM_PAINT()
    ON_WM_QUERYDRAGICON()
    //}}AFX_MSG_MAP
    ON_BN_CLICKED(IDC_LOGIN, OnLogin)
END_MESSAGE_MAP()

BOOL
CChatConfigDlg::OnInitDialog()
{
    CDialog::OnInitDialog();

    // Set the icon for this dialog.  The framework does this automatically
    // when the application's main window is not a dialog
    SetIcon(_hIcon, TRUE);         // Set big icon
    SetIcon(_hIcon, FALSE);        // Set small icon

    //
    // Retrieve the text input edit control.
    //
    _useredit = (CEdit*)GetDlgItem(IDC_USER);
    _passedit = (CEdit*)GetDlgItem(IDC_PASSWORD);
    _hostedit = (CEdit*)GetDlgItem(IDC_HOST);
    _portedit = (CEdit*)GetDlgItem(IDC_PORT);

    //
    // Set the focus to the username input
    //
    _useredit->SetFocus();
 
    return FALSE; // return FALSE because we explicitly set the focus
}

void
CChatConfigDlg::OnCancel()
{
    CDialog::OnCancel();
}

// If you add a minimize button to your dialog, you will need the code below
// to draw the icon.  For MFC applications using the document/view model,
// this is automatically done for you by the framework.

void
CChatConfigDlg::OnPaint() 
{
#ifdef _WIN32_WCE
    CDialog::OnPaint();
#else
    if (IsIconic())
    {
        CPaintDC dc(this); // device context for painting

        SendMessage(WM_ICONERASEBKGND, reinterpret_cast<WPARAM>(dc.GetSafeHdc()), 0);

        // Center icon in client rectangle
        int cxIcon = GetSystemMetrics(SM_CXICON);
        int cyIcon = GetSystemMetrics(SM_CYICON);
        CRect rect;
        GetClientRect(&rect);
        int x = (rect.Width() - cxIcon + 1) / 2;
        int y = (rect.Height() - cyIcon + 1) / 2;

        // Draw the icon
        dc.DrawIcon(x, y, _hIcon);
    }
    else
    {
        CDialog::OnPaint();
    }
#endif
}

// The system calls this function to obtain the cursor to display while the user drags
// the minimized window.
HCURSOR
CChatConfigDlg::OnQueryDragIcon()
{
    return static_cast<HCURSOR>(_hIcon);
}


void
CChatConfigDlg::OnLogin()
{
    CString user, password, host, port;

    //
    // Read the username.
    //
    int len = _useredit->LineLength();
    _useredit->GetLine(0, user.GetBuffer(len), len);
    user.ReleaseBuffer(len);

    //
    // Read the password.
    //
    len = _passedit->LineLength();
    _passedit->GetLine(0, password.GetBuffer(len), len);
    password.ReleaseBuffer(len);

    //
    // Read the host.
    //
    len = _hostedit->LineLength();
    _hostedit->GetLine(0, host.GetBuffer(len), len);
    host.ReleaseBuffer(len);

    //
    // Read the port.
    //
    len = _portedit->LineLength();
    _portedit->GetLine(0, port.GetBuffer(len), len);
    port.ReleaseBuffer(len);

    bool success = false;
    try
    {
    	std::string routerStr = Ice::printfToString("Glacier2/router:tcp -p %s -h %s", port, host);

	Ice::RouterPrx defaultRouter = Ice::RouterPrx::uncheckedCast(_communicator->stringToProxy(routerStr));
        _communicator->setDefaultRouter(defaultRouter);

	Ice::PropertiesPtr properties = _communicator->getProperties();
	properties->setProperty("Chat.Client.Router", routerStr);
	properties->setProperty("Chat.Client.Endpoints", "");

        Glacier2::RouterPrx router = Glacier2::RouterPrx::checkedCast(defaultRouter);
        if(router)
	{
            Demo::ChatSessionPrx session = 
	        Demo::ChatSessionPrx::uncheckedCast(router->createSession(std::string(user), std::string(password)));

            std::string category = router->getServerProxy()->ice_getIdentity().category;
            Ice::Identity callbackReceiverIdent;
            callbackReceiverIdent.name = "callbackReceiver";
            callbackReceiverIdent.category = category;

            Ice::ObjectAdapterPtr adapter = _communicator->createObjectAdapter("Chat.Client");
            Demo::ChatCallbackPrx callback = Demo::ChatCallbackPrx::uncheckedCast(
                adapter->add(new ChatCallbackI(_log), callbackReceiverIdent));
            adapter->activate();

            session->setCallback(callback);
	    _mainDiag->setSession(session);
	    success = true;
	}
	else
        {
            AfxMessageBox(CString("Configured router is not a Glacier2 router"), MB_OK|MB_ICONEXCLAMATION);
        }

    }
    catch(const Ice::Exception& ex)
    {
        AfxMessageBox(CString(ex.toString().c_str()), MB_OK|MB_ICONEXCLAMATION);
    }
    
    if(success)
    {
        EndDialog(0);
    }
}
