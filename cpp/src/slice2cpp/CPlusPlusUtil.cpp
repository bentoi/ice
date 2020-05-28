//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#include "CPlusPlusUtil.h"
#include <Slice/Util.h>
#include <cstring>
#include <functional>

#ifndef _WIN32
#  include <fcntl.h>
#endif

using namespace std;
using namespace Slice;
using namespace IceUtil;
using namespace IceUtilInternal;

namespace
{

string toTemplateArg(const string& arg)
{
    if(arg.empty())
    {
        return arg;
    }
    string fixed = arg;
    if(arg[0] == ':')
    {
        fixed = " " + fixed;
    }
    if(fixed[fixed.length() - 1] == '>')
    {
        fixed = fixed + " ";
    }
    return fixed;
}

string
toOptional(const string& s)
{
    return "Ice::optional<" + s + ">";
}

bool
isOptionalProxyOrClass(const TypePtr& type)
{
    if (auto optional = OptionalPtr::dynamicCast(type))
    {
        auto underlying = optional->underlying();
        if (InterfaceDeclPtr::dynamicCast(underlying) || ClassDeclPtr::dynamicCast(underlying))
        {
            return true;
        }
        auto builtin = BuiltinPtr::dynamicCast(underlying);
        if (builtin && (builtin->kind() == Builtin::KindObject || builtin->kind() == Builtin::KindValue))
        {
            return true;
        }
    }
    return false;
}

string
stringTypeToString(const TypePtr&, const StringList& metaData, int typeCtx)
{
    string strType = findMetaData(metaData, typeCtx);
    if(strType == "wstring" || (typeCtx & TypeContextUseWstring && strType == ""))
    {
        return "::std::wstring";
    }
    else if(strType != "" && strType != "string")
    {
        return strType;
    }
    else
    {
        return "::std::string";
    }
}

string
sequenceTypeToString(const SequencePtr& seq, const string& scope, const StringList& metaData, int typeCtx)
{
    string seqType = findMetaData(metaData, typeCtx);
    if(!seqType.empty())
    {
        if(seqType == "%array")
        {
            BuiltinPtr builtin = BuiltinPtr::dynamicCast(seq->type());
            if(typeCtx & TypeContextAMIPrivateEnd)
            {
                if(builtin && builtin->kind() == Builtin::KindByte)
                {
                    string s = typeToString(seq->type(), scope);
                    return "::std::pair<const " + s + "*, const " + s + "*>";
                }
                else if(builtin &&
                        builtin->kind() != Builtin::KindString &&
                        builtin->kind() != Builtin::KindObject)
                {
                    string s = toTemplateArg(typeToString(builtin, scope));
                    return "::std::pair< ::IceUtil::ScopedArray<" + s + ">, " +
                        "::std::pair<const " + s + "*, const " + s + "*> >";
                }
                else
                {
                    string s = toTemplateArg(typeToString(seq->type(), scope, seq->typeMetaData(),
                                                          inWstringModule(seq) ? TypeContextUseWstring : 0));
                    return "::std::vector<" + s + '>';
                }
            }
            string s = typeToString(seq->type(), scope, seq->typeMetaData(),
                                    typeCtx | (inWstringModule(seq) ? TypeContextUseWstring : 0));
            return "::std::pair<const " + s + "*, const " + s + "*>";
        }
        else
        {
            return seqType;
        }
    }
    else
    {
        return getUnqualified(fixKwd(seq->scoped()), scope);
    }
}

string
dictionaryTypeToString(const DictionaryPtr& dict, const string& scope, const StringList& metaData, int typeCtx)
{
    const string dictType = findMetaData(metaData, typeCtx);
    if(dictType.empty())
    {
        return getUnqualified(fixKwd(dict->scoped()), scope);
    }
    else
    {
        return dictType;
    }
}

void
writeParamAllocateCode(Output& out, const TypePtr& type, bool isTagged, const string& scope, const string& fixedName,
                       const StringList& metaData, int typeCtx)
{
    string s = typeToString(unwrapIfOptional(type), scope, metaData, typeCtx);
    if(isTagged)
    {
        s = toOptional(s);
    }
    out << nl << s << ' ' << fixedName << ';';
}

void
writeMarshalUnmarshalParams(Output& out, const ParamDeclList& params, const OperationPtr& op, bool marshal,
                            bool prepend, const string& customStream = "", const string& retP = "",
                            const string& obj = "")
{
    string prefix = prepend ? paramPrefix : "";
    string returnValueS = retP.empty() ? string("ret") : retP;
    string objPrefix = obj.empty() ? obj : obj + ".";

    string stream = customStream;
    if(stream.empty())
    {
        stream = marshal ? "ostr" : "istr";
    }

    //
    // Marshal required parameters.
    //
    ParamDeclList requiredParams;
    ParamDeclList taggedParams;
    for(ParamDeclList::const_iterator p = params.begin(); p != params.end(); ++p)
    {
        if((*p)->tagged())
        {
            taggedParams.push_back(*p);
        }
        else
        {
            requiredParams.push_back(*p);
        }
    }

    if(!requiredParams.empty() || (op && op->returnType() && !op->returnIsTagged()))
    {
        out << nl;
        if(marshal)
        {
            out << stream << "->writeAll";
        }
        else
        {
            out << stream << "->readAll";
        }
        out << spar;
        for(ParamDeclList::const_iterator p = requiredParams.begin(); p != requiredParams.end(); ++p)
        {
            out << objPrefix + fixKwd(prefix + (*p)->name());
        }
        if(op && op->returnType() && !op->returnIsTagged())
        {
            out << objPrefix + returnValueS;
        }
        out << epar << ";";
    }

    if(!taggedParams.empty() || (op && op->returnType() && op->returnIsTagged()))
    {
        //
        // Sort tagged parameters by tag.
        //
        class SortFn
        {
        public:
            static bool compare(const ParamDeclPtr& lhs, const ParamDeclPtr& rhs)
            {
                return lhs->tag() < rhs->tag();
            }
        };
        taggedParams.sort(SortFn::compare);

        out << nl;
        if(marshal)
        {
            out << stream << "->writeAll";
        }
        else
        {
            out << stream << "->readAll";
        }
        out << spar;

        {
            //
            // Tags
            //
            ostringstream os;
            os << '{';
            bool checkReturnType = op && op->returnIsTagged();
            bool insertComma = false;
            for(ParamDeclList::const_iterator p = taggedParams.begin(); p != taggedParams.end(); ++p)
            {
                if(checkReturnType && op->returnTag() < (*p)->tag())
                {
                    os << (insertComma ? ", " : "") << op->returnTag();
                    checkReturnType = false;
                    insertComma = true;
                }
                os << (insertComma ? ", " : "") << (*p)->tag();
                insertComma = true;
            }
            if(checkReturnType)
            {
                os << (insertComma ? ", " : "") << op->returnTag();
            }
            os << '}';
            out << os.str();
        }

        {
            //
            // Parameters
            //
            bool checkReturnType = op && op->returnIsTagged();
            for(ParamDeclList::const_iterator p = taggedParams.begin(); p != taggedParams.end(); ++p)
            {
                if(checkReturnType && op->returnTag() < (*p)->tag())
                {
                    out << objPrefix + returnValueS;
                    checkReturnType = false;
                }
                out << objPrefix + fixKwd(prefix + (*p)->name());
            }
            if(checkReturnType)
            {
                out << objPrefix + returnValueS;
            }
        }
        out << epar << ";";
    }
}
}

string Slice::paramPrefix = "iceP_";

char
Slice::ToIfdef::operator()(char c)
{
    if(!isalnum(static_cast<unsigned char>(c)))
    {
        return '_';
    }
    else
    {
        return c;
    }
}

void
Slice::printHeader(Output& out)
{
    static const char* header =
"//\n"
"// Copyright (c) ZeroC, Inc. All rights reserved.\n"
"//\n"
        ;

    out << header;
    out << "//\n";
    out << "// Ice version " << ICE_STRING_VERSION << "\n";
    out << "//\n";
}

void
Slice::printVersionCheck(Output& out)
{
    out << "\n";
    out << "\n#ifndef ICE_IGNORE_VERSION";
#if defined(ICE_ALPHA_VERSION)
    // Exact matches are required for alpha generated code.
    out << "\n#   if ICE_INT_VERSION  != " << ICE_INT_VERSION <<
            " || !defined(ICE_ALPHA_VERSION) || ICE_ALPHA_VERSION != " << ICE_ALPHA_VERSION;
    out << "\n#       error Ice version mismatch: an exact match is required for alpha generated code";
    out << "\n#   endif";
#elif defined(ICE_BETA_VERSION)
    // Exact matches are required for beta generated code.
    out << "\n#   if ICE_INT_VERSION  != " << ICE_INT_VERSION <<
            " || !defined(ICE_BETA_VERSION) || ICE_BETA_VERSION != " << ICE_BETA_VERSION;
    out << "\n#       error Ice version mismatch: an exact match is required for beta generated code";
    out << "\n#   endif";
#else
    out << "\n#   if ICE_INT_VERSION / 100 != " << ICE_INT_VERSION / 100;
    out << "\n#       error Ice version mismatch!";
    out << "\n#   endif";
    out << "\n#   if ICE_INT_VERSION % 100 < " << ICE_INT_VERSION % 100;
    out << "\n#       error Ice patch level mismatch!";
    out << "\n#   endif";
    //
    // Generated code is release; reject pre-release header files.
    //
    out << "\n#   if defined(ICE_ALPHA_VERSION) || defined(ICE_BETA_VERSION)";
    out << "\n#       error Pre-release header file detected";
    out << "\n#   endif";
#endif

    out << "\n#endif";
}

void
Slice::printDllExportStuff(Output& out, const string& dllExport)
{
    if(dllExport.size())
    {
        out << sp;
        out << "\n#ifndef " << dllExport;
        out << "\n#   if defined(ICE_STATIC_LIBS)";
        out << "\n#       define " << dllExport << " /**/";
        out << "\n#   elif defined(" << dllExport << "_EXPORTS)";
        out << "\n#       define " << dllExport << " ICE_DECLSPEC_EXPORT";
        out << "\n#   else";
        out << "\n#       define " << dllExport << " ICE_DECLSPEC_IMPORT";
        out << "\n#   endif";
        out << "\n#endif";
    }
}

bool
Slice::isMovable(const TypePtr& type)
{
    BuiltinPtr builtin = BuiltinPtr::dynamicCast(type);
    if(builtin)
    {
        switch(builtin->kind())
        {
            case Builtin::KindString:
            case Builtin::KindObject:
            case Builtin::KindValue:
            {
                return true;
            }
            default:
            {
                return false;
            }
        }
    }
    return !EnumPtr::dynamicCast(type);
}

string
Slice::getUnqualified(const std::string& type, const std::string& scope)
{
    if(type.find("::") != string::npos)
    {
        string prefix;
        if(type.find("const ") == 0)
        {
            prefix += "const ";
        }

        if(type.find("::std::shared_ptr<", prefix.size()) == prefix.size())
        {
            prefix += "::std::shared_ptr<";
        }

        if(type.find(scope, prefix.size()) == prefix.size())
        {
            string t = type.substr(prefix.size() + scope.size());
            if(t.find("::") == string::npos)
            {
                return prefix + t;
            }
        }
    }
    return type;
}

string
Slice::typeToString(const TypePtr& type, const string& scope, const StringList& metaData, int typeCtx)
{
    static const std::array<std::string, 17> builtinTable =
    {
        "bool",
        "::Ice::Byte",
        "short",
        "unsigned short",
        "int",
        "unsigned int",
        "int",
        "unsigned int",
        "long long int",
        "unsigned long long int",
        "long long int",
        "unsigned long long int",
        "float",
        "double",
        "::std::string",
        "::std::shared_ptr<::Ice::ObjectPrx>",
        "::std::shared_ptr<::Ice::Value>"
    };

    if (isOptionalProxyOrClass(type))
    {
        return typeToString(OptionalPtr::dynamicCast(type)->underlying(), scope, metaData, typeCtx);
    }

    BuiltinPtr builtin = BuiltinPtr::dynamicCast(type);
    if(builtin)
    {
        if(builtin->kind() == Builtin::KindString)
        {
            return stringTypeToString(type, metaData, typeCtx);
        }
        else
        {
            return getUnqualified(builtinTable[builtin->kind()], scope);
        }
    }

    ClassDeclPtr cl = ClassDeclPtr::dynamicCast(type);
    if(cl)
    {
        return "::std::shared_ptr<" + getUnqualified(cl->scoped(), scope) + ">";
    }

    InterfaceDeclPtr proxy = InterfaceDeclPtr::dynamicCast(type);
    if(proxy)
    {
       return "::std::shared_ptr<" + getUnqualified(fixKwd(proxy->scoped() + "Prx"), scope) + ">";
    }

    StructPtr st = StructPtr::dynamicCast(type);
    if(st)
    {
        return getUnqualified(fixKwd(st->scoped()), scope);
    }

    EnumPtr en = EnumPtr::dynamicCast(type);
    if(en)
    {
        return getUnqualified(fixKwd(en->scoped()), scope);
    }

    SequencePtr seq = SequencePtr::dynamicCast(type);
    if(seq)
    {
        return sequenceTypeToString(seq, scope, metaData, typeCtx);
    }

    DictionaryPtr dict = DictionaryPtr::dynamicCast(type);
    if(dict)
    {
        return dictionaryTypeToString(dict, scope, metaData, typeCtx);
    }

    assert(0);
    return "???";
}

string
Slice::typeToString(const TypePtr& type, bool tagged, const string& scope, const StringList& metaData, int typeCtx)
{
    if(tagged) // meaning tagged
    {
        return toOptional(typeToString(OptionalPtr::dynamicCast(type)->underlying(), scope, metaData, typeCtx));
    }
    else
    {
        return typeToString(type, scope, metaData, typeCtx);
    }
}

string
Slice::returnTypeToString(const TypePtr& type, bool tagged, const string& scope, const StringList& metaData,
                          int typeCtx)
{
    if(!type)
    {
        return "void";
    }

    if(tagged)
    {
        return toOptional(typeToString(OptionalPtr::dynamicCast(type)->underlying(), scope, metaData, typeCtx));
    }

    return typeToString(type, scope, metaData, typeCtx);
}

string
Slice::inputTypeToString(const TypePtr& type, bool tagged, const string& scope, const StringList& metaData,
                         int typeCtx)
{
    static const std::array<std::string, 17> inputBuiltinTable =
    {
        "bool",
        "::Ice::Byte",
        "short",
        "unsigned short",
        "int",
        "unsigned int",
        "int",
        "unsigned int",
        "long long int",
        "unsigned long long int",
        "long long int",
        "unsigned long long int",
        "float",
        "double",
        "const ::std::string&",
        "const ::std::shared_ptr<::Ice::ObjectPrx>&",
        "const ::std::shared_ptr<::Ice::Value>&"
    };

    typeCtx |= TypeContextInParam;

    if(tagged)
    {
        return "const " + toOptional(typeToString(OptionalPtr::dynamicCast(type)->underlying(), scope, metaData,
            typeCtx)) + '&';
    }

    if (isOptionalProxyOrClass(type))
    {
        return inputTypeToString(OptionalPtr::dynamicCast(type)->underlying(), tagged, scope, metaData, typeCtx);
    }

    BuiltinPtr builtin = BuiltinPtr::dynamicCast(type);
    if(builtin)
    {
        if(builtin->kind() == Builtin::KindString)
        {
            return string("const ") + stringTypeToString(type, metaData, typeCtx) + '&';
        }
        else
        {
            return getUnqualified(inputBuiltinTable[builtin->kind()], scope);
        }
    }

    ClassDeclPtr cl = ClassDeclPtr::dynamicCast(type);
    if(cl)
    {
        return "const ::std::shared_ptr<" + getUnqualified(fixKwd(cl->scoped()), scope) + ">&";
    }

    StructPtr st = StructPtr::dynamicCast(type);
    if(st)
    {
        return "const " + getUnqualified(fixKwd(st->scoped()), scope) + "&";
    }

    InterfaceDeclPtr proxy = InterfaceDeclPtr::dynamicCast(type);
    if(proxy)
    {
        return "const ::std::shared_ptr<" + getUnqualified(fixKwd(proxy->scoped() + "Prx"), scope) + ">&";
    }

    EnumPtr en = EnumPtr::dynamicCast(type);
    if(en)
    {
        return getUnqualified(fixKwd(en->scoped()), scope);
    }

    SequencePtr seq = SequencePtr::dynamicCast(type);
    if(seq)
    {
        return "const " + sequenceTypeToString(seq, scope, metaData, typeCtx) + "&";
    }

    DictionaryPtr dict = DictionaryPtr::dynamicCast(type);
    if(dict)
    {
        return "const " + dictionaryTypeToString(dict, scope, metaData, typeCtx) + "&";
    }

    assert(0);
    return "???";
}

string
Slice::outputTypeToString(const TypePtr& type, bool tagged, const string& scope, const StringList& metaData,
                          int typeCtx)
{
    static const std::array<std::string, 17> outputBuiltinTable =
    {
        "bool&",
        "::Ice::Byte&",
        "short&",
        "unsigned short&",
        "int&",
        "unsigned int&",
        "int&",
        "unsigned int&",
        "long long int&",
        "unsigned long long int&",
        "long long int&",
        "unsigned long long int&",
        "float&",
        "double&",
        "::std::string&",
        "::std::shared_ptr<::Ice::ObjectPrx>&",
        "::std::shared_ptr<::Ice::Value>&"
    };

    if(tagged)
    {
        return toOptional(typeToString(OptionalPtr::dynamicCast(type)->underlying(), scope, metaData, typeCtx)) + '&';
    }

    if (isOptionalProxyOrClass(type))
    {
        return outputTypeToString(OptionalPtr::dynamicCast(type)->underlying(), tagged, scope, metaData, typeCtx);
    }

    BuiltinPtr builtin = BuiltinPtr::dynamicCast(type);
    if(builtin)
    {
        if(builtin->kind() == Builtin::KindString)
        {
            return stringTypeToString(type, metaData, typeCtx) + "&";
        }
        else
        {
            return getUnqualified(outputBuiltinTable[builtin->kind()], scope);
        }
    }

    ClassDeclPtr cl = ClassDeclPtr::dynamicCast(type);
    if(cl)
    {
        return "::std::shared_ptr<" + getUnqualified(fixKwd(cl->scoped()), scope) + ">&";
    }

    StructPtr st = StructPtr::dynamicCast(type);
    if(st)
    {
        return getUnqualified(fixKwd(st->scoped()), scope) + "&";
    }

    InterfaceDeclPtr proxy = InterfaceDeclPtr::dynamicCast(type);
    if(proxy)
    {
        return "::std::shared_ptr<" + getUnqualified(fixKwd(proxy->scoped() + "Prx"), scope) + ">&";
    }

    EnumPtr en = EnumPtr::dynamicCast(type);
    if(en)
    {
        return getUnqualified(fixKwd(en->scoped()), scope) + "&";
    }

    SequencePtr seq = SequencePtr::dynamicCast(type);
    if(seq)
    {
        return sequenceTypeToString(seq, scope, metaData, typeCtx) + "&";
    }

    DictionaryPtr dict = DictionaryPtr::dynamicCast(type);
    if(dict)
    {
        return dictionaryTypeToString(dict, scope, metaData, typeCtx) + "&";
    }

    assert(0);
    return "???";
}

string
Slice::operationModeToString(Operation::Mode mode)
{
    switch(mode)
    {
    case Operation::Normal:
        return"::Ice::OperationMode::Normal";
    case Operation::Nonmutating:
        return"::Ice::OperationMode::Nonmutating";
    case Operation::Idempotent:
        return"::Ice::OperationMode::Idempotent";
    }
    throw logic_error("");
}

string
Slice::opFormatTypeToString(const OperationPtr& op)
{
    switch(op->format())
    {
    case DefaultFormat:
        return "::Ice::FormatType::DefaultFormat";
    case CompactFormat:
        return "::Ice::FormatType::CompactFormat";
    case SlicedFormat:
        return "::Ice::FormatType::SlicedFormat";
    }
    throw logic_error("");
}

//
// If the passed name is a keyword, return the name with a "_cpp_" prefix;
// otherwise, return the name unchanged.
//

static string
lookupKwd(const string& name)
{
    //
    // Keyword list. *Must* be kept in alphabetical order.
    //
    // Note that this keyword list unnecessarily contains C++ keywords
    // that are illegal Slice identifiers -- namely identifiers that
    // are Slice keywords (class, int, etc.). They have not been removed
    // so that the keyword list is kept complete.
    //
    static const string keywordList[] =
    {
        "alignas", "alignof", "and", "and_eq", "asm", "auto", "bit_and", "bit_or", "bool", "break",
        "case", "catch", "char", "char16_t", "char32_t", "class", "compl", "const", "const_exptr", "const_cast", "continue",
        "decltype", "default", "delete", "do", "double", "dynamic_cast",
        "else", "enum", "explicit", "export", "extern", "false", "float", "for", "friend",
        "goto", "if", "inline", "int", "long", "mutable", "namespace", "new", "noexcept", "not", "not_eq",
        "operator", "or", "or_eq", "private", "protected", "public", "register", "reinterpret_cast", "return",
        "short", "signed", "sizeof", "static", "static_assert", "static_cast", "struct", "switch",
        "template", "this", "thread_local", "throw", "true", "try", "typedef", "typeid", "typename",
        "union", "unsigned", "using", "virtual", "void", "volatile", "wchar_t", "while", "xor", "xor_eq"
    };
    bool found =  binary_search(&keywordList[0],
                                &keywordList[sizeof(keywordList) / sizeof(*keywordList)],
                                name);
    return found ? "_cpp_" + name : name;
}

//
// If the passed name is a scoped name, return the identical scoped name,
// but with all components that are C++ keywords replaced by
// their "_cpp_"-prefixed version; otherwise, if the passed name is
// not scoped, but a C++ keyword, return the "_cpp_"-prefixed name;
// otherwise, return the name unchanged.
//
string
Slice::fixKwd(const string& name)
{
    if(name[0] != ':')
    {
        return lookupKwd(name);
    }
    auto ids = splitScopedName(name);
    transform(ids.begin(), ids.end(), ids.begin(), lookupKwd);
    stringstream result;
    for(auto id : ids)
    {
        result << "::" + id;
    }
    return result.str();
}

void
Slice::writeMarshalCode(Output& out, const ParamDeclList& params, const OperationPtr& op, bool prepend,
                        const string& customStream, const string& retP)
{
    writeMarshalUnmarshalParams(out, params, op, true, prepend, customStream, retP);
}

void
Slice::writeUnmarshalCode(Output& out, const ParamDeclList& params, const OperationPtr& op, bool prepend,
                          const string& customStream, const string& retP, const string& obj)
{
    writeMarshalUnmarshalParams(out, params, op, false, prepend, customStream, retP, obj);
}

void
Slice::writeAllocateCode(Output& out, const ParamDeclList& params, const OperationPtr& op, bool prepend,
                         const string& clScope, int typeCtx, const string& customRet)
{
    string prefix = prepend ? paramPrefix : "";
    string returnValueS = customRet;
    if(returnValueS.empty())
    {
        returnValueS = "ret";
    }

    for(ParamDeclList::const_iterator p = params.begin(); p != params.end(); ++p)
    {
        writeParamAllocateCode(out, (*p)->type(), (*p)->tagged(), clScope, fixKwd(prefix + (*p)->name()),
                               (*p)->getMetaData(), typeCtx);
    }

    if(op && op->returnType())
    {
        writeParamAllocateCode(out, op->returnType(), op->returnIsTagged(), clScope, returnValueS, op->getMetaData(),
                               typeCtx);
    }
}

void
Slice::writeMarshalUnmarshalAllInHolder(IceUtilInternal::Output& out,
                                          const string& holder,
                                          const DataMemberList& dataMembers,
                                          bool isTagged,
                                          bool marshal)
{
    if(dataMembers.empty())
    {
        return;
    }

    string stream = marshal ? "ostr" : "istr";
    string streamOp = marshal ? "writeAll" : "readAll";

    out << nl << stream << "->" << streamOp;
    out << spar;

    if(isTagged)
    {
        ostringstream os;
        os << "{";
        for(DataMemberList::const_iterator q = dataMembers.begin(); q != dataMembers.end(); ++q)
        {
            if(q != dataMembers.begin())
            {
                os << ", ";
            }
            os << (*q)->tag();
        }
        os << "}";
        out << os.str();
    }

    for(DataMemberList::const_iterator q = dataMembers.begin(); q != dataMembers.end(); ++q)
    {
        out << holder + fixKwd((*q)->name());
    }

    out << epar << ";";

}

void
Slice::writeStreamHelpers(Output& out,
                          const ContainedPtr& c,
                          DataMemberList dataMembers,
                          bool hasBaseDataMembers)
{
    // If c is a class/exception whose base class contains data members (recursively), then we need to generate
    // a StreamWriter even if its implementation is empty. This is because our default marshaling uses ice_tuple() which
    // contains all of our class/exception's data members as well the base data members, which breaks marshaling. This
    // is not an issue for structs.
    if(dataMembers.empty() && !hasBaseDataMembers)
    {
        return;
    }

    DataMemberList requiredMembers;
    DataMemberList taggedMembers;

    for(DataMemberList::const_iterator q = dataMembers.begin(); q != dataMembers.end(); ++q)
    {
        if((*q)->tagged())
        {
            taggedMembers.push_back(*q);
        }
        else
        {
            requiredMembers.push_back(*q);
        }
    }

    // Sort tagged data members
    class SortFn
    {
    public:
        static bool compare(const DataMemberPtr& lhs, const DataMemberPtr& rhs)
        {
            return lhs->tag() < rhs->tag();
        }
    };
    taggedMembers.sort(SortFn::compare);

    string scoped = c->scoped();
    string fullName = fixKwd(scoped);
    string holder = "v.";

    //
    // Generate StreamWriter
    //
    // Only generate StreamWriter specializations if we are generating
    // with tagged data members and no base class data members
    //
    if(!taggedMembers.empty() || hasBaseDataMembers)
    {
        out << nl << "template<typename S>";
        out << nl << "struct StreamWriter<" << fullName << ", S>";
        out << sb;
        if(requiredMembers.empty() && taggedMembers.empty())
        {
            out << nl << "static void write(S*, const " << fullName << "&)";
        }
        else
        {
            out << nl << "static void write(S* ostr, const " << fullName << "& v)";
        }

        out << sb;

        writeMarshalUnmarshalAllInHolder(out, holder, requiredMembers, false, true);
        writeMarshalUnmarshalAllInHolder(out, holder, taggedMembers, true, true);

        out << eb;
        out << eb << ";" << nl;
    }

    //
    // Generate StreamReader
    //
    out << nl << "template<typename S>";
    out << nl << "struct StreamReader<" << fullName << ", S>";
    out << sb;
    if (requiredMembers.empty() && taggedMembers.empty())
    {
        out << nl << "static void read(S*, " << fullName << "&)";
    }
    else
    {
        out << nl << "static void read(S* istr, " << fullName << "& v)";
    }

    out << sb;

    writeMarshalUnmarshalAllInHolder(out, holder, requiredMembers, false, false);
    writeMarshalUnmarshalAllInHolder(out, holder, taggedMembers, true, false);

    out << eb;
    out << eb << ";" << nl;
}

void
Slice::writeIceTuple(::IceUtilInternal::Output& out, DataMemberList dataMembers, int typeCtx)
{
    //
    // Use an empty scope to get full qualified names from calls to typeToString.
    //
    const string scope = "";
    out << nl << "std::tuple<";
    for(DataMemberList::const_iterator q = dataMembers.begin(); q != dataMembers.end(); ++q)
    {
        if(q != dataMembers.begin())
        {
            out << ", ";
        }
        out << "const ";
        out << typeToString((*q)->type(), (*q)->tagged(), scope, (*q)->getMetaData(), typeCtx)
            << "&";
    }
    out << "> ice_tuple() const";

    out << sb;
    out << nl << "return std::tie(";
    for(DataMemberList::const_iterator pi = dataMembers.begin(); pi != dataMembers.end(); ++pi)
    {
        if(pi != dataMembers.begin())
        {
            out << ", ";
        }
        out << fixKwd((*pi)->name());
    }
    out << ");" << eb;
}

bool
Slice::findMetaData(const string& prefix, const ClassDeclPtr& cl, string& value)
{
    if(findMetaData(prefix, cl->getMetaData(), value))
    {
        return true;
    }

    ClassDefPtr def = cl->definition();
    return def ? findMetaData(prefix, def->getMetaData(), value) : false;
}

bool
Slice::findMetaData(const string& prefix, const StringList& metaData, string& value)
{
    for(StringList::const_iterator i = metaData.begin(); i != metaData.end(); i++)
    {
        string s = *i;
        if(s.find(prefix) == 0)
        {
            value = s.substr(prefix.size());
            return true;
        }
    }
    return false;
}

string
Slice::findMetaData(const StringList& metaData, int typeCtx)
{
    static const string prefix = "cpp:";

    for(StringList::const_iterator q = metaData.begin(); q != metaData.end(); ++q)
    {
        string str = *q;
        if(str.find(prefix) == 0)
        {
            string::size_type pos = str.find(':', prefix.size());

            //
            // If the form is cpp:type:<...> the data after cpp:type:
            // is returned.
            // If the form is cpp:view-type:<...> the data after the
            // cpp:view-type: is returned
            //
            // The priority of the metadata is as follows:
            // 1: array, view-type for "view" parameters
            // 2: unscoped
            //

            if(pos != string::npos)
            {
                string ss = str.substr(prefix.size());

                if(typeCtx & (TypeContextInParam | TypeContextAMIPrivateEnd))
                {
                    if(ss.find("view-type:") == 0)
                    {
                        return str.substr(pos + 1);
                    }
                }

                if(ss.find("type:") == 0)
                {
                    return str.substr(pos + 1);
                }
            }
            else if(typeCtx & (TypeContextInParam | TypeContextAMIPrivateEnd))
            {
                string ss = str.substr(prefix.size());
                if(ss == "array")
                {
                    return "%array";
                }
            }
            //
            // Otherwise if the data is "unscoped" it is returned.
            //
            else
            {
                string ss = str.substr(prefix.size());
                if(ss == "unscoped")
                {
                    return "%unscoped";
                }
            }
        }
    }

    return "";
}

bool
Slice::inWstringModule(const SequencePtr& seq)
{
    ContainerPtr cont = seq->container();
    while(cont)
    {
        ModulePtr mod = ModulePtr::dynamicCast(cont);
        if(!mod)
        {
            break;
        }
        StringList metaData = mod->getMetaData();
        if(find(metaData.begin(), metaData.end(), "cpp:type:wstring") != metaData.end())
        {
            return true;
        }
        else if(find(metaData.begin(), metaData.end(), "cpp:type:string") != metaData.end())
        {
            return false;
        }
        cont = mod->container();
    }
    return false;
}

string
Slice::getDataMemberRef(const DataMemberPtr& p)
{
    string name = fixKwd(p->name());
    if(!p->tagged())
    {
        return name;
    }

    if(BuiltinPtr::dynamicCast(p->type()))
    {
        return "*" + name;
    }
    else
    {
        return "(*" + name + ")";
    }
}
