//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#include <IceUtil/Functional.h>
#include <IceUtil/InputUtil.h>
#include <IceUtil/StringUtil.h>
#include <Slice/Parser.h>
#include <Slice/GrammarUtil.h>
#include <cstring>
#include <iterator>

// TODO: fix this warning once we no longer support VS2013 and earlier
#if defined(_MSC_VER)
#    pragma warning(disable:4589) // Constructor of abstract class 'Slice::Type' ignores initializer...
#endif

using namespace std;
using namespace Slice;

Slice::CompilerException::CompilerException(const char* file, int line, const string& r) :
    IceUtil::Exception(file, line),
    _reason(r)
{
}

string
Slice::CompilerException::ice_id() const
{
    return "::Slice::CompilerException";
}

void
Slice::CompilerException::ice_print(ostream& out) const
{
    IceUtil::Exception::ice_print(out);
    out << ": " << _reason;
}

Slice::CompilerException*
Slice::CompilerException::ice_cloneImpl() const
{
    return new CompilerException(*this);
}

void
Slice::CompilerException::ice_throw() const
{
    throw *this;
}

string
Slice::CompilerException::reason() const
{
    return _reason;
}

// Forward declare things from Bison and Flex the parser can use.
extern int slice_parse();
extern int slice_lineno;
extern FILE* slice_in;
extern int slice_debug;
extern int slice__flex_debug;

//
// Operation attributes
//
// read + supports must be 0 (the default)
//

namespace
{

string readWriteAttribute[] = { "read", "write" };
string txAttribute[] = { "supports", "mandatory", "required", "never" };
enum { Supports, Mandatory, Required, Never };

string
prependA(const string& s) // return a or an <s>
{
    static const string vowels = "aeiou";
    string prefix = "a";
    if (vowels.find_first_of(s[0]) != string::npos)
    {
        prefix += "n";
    }
    return prefix + " " + s;
}

DataMemberList
filterSortedTaggedDataMembers(const DataMemberList& members)
{
    class SortFn
    {
    public:
        static bool compare(const DataMemberPtr& lhs, const DataMemberPtr& rhs)
        {
            return lhs->tag() < rhs->tag();
        }
    };

    DataMemberList result;
    for (DataMemberList::const_iterator p = members.begin(); p != members.end(); ++p)
    {
        if((*p)->tagged())
        {
            result.push_back(*p);
        }
    }
    result.sort(SortFn::compare);
    return result;
}

void
sortTaggedParameters(ParamDeclList& params)
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
    params.sort(SortFn::compare);
}

bool
isMutableAfterReturnType(const TypePtr& type)
{
    //
    // Returns true if the type contains data types which can be referenced by user code
    // and mutated after a dispatch returns.
    //

    if (auto optional = OptionalPtr::dynamicCast(type))
    {
        return isMutableAfterReturnType(optional->underlying());
    }

    if(ClassDeclPtr::dynamicCast(type))
    {
        return true;
    }

    BuiltinPtr builtin = BuiltinPtr::dynamicCast(type);
    if(builtin && builtin->usesClasses())
    {
        return true;
    }

    if(SequencePtr::dynamicCast(type) || DictionaryPtr::dynamicCast(type))
    {
        return true;
    }

    StructPtr s = StructPtr::dynamicCast(type);
    if(s)
    {
        return true;
    }

    return false;
}

}

namespace Slice
{

Unit* unit;

}

// ----------------------------------------------------------------------
// DefinitionContext
// ----------------------------------------------------------------------

Slice::DefinitionContext::DefinitionContext(int includeLevel, const StringList& metaData) :
    _includeLevel(includeLevel), _metaData(metaData)
{
    initSuppressedWarnings();
}

string
Slice::DefinitionContext::filename() const
{
    return _filename;
}

int
Slice::DefinitionContext::includeLevel() const
{
    return _includeLevel;
}

void
Slice::DefinitionContext::setFilename(const string& filename)
{
    _filename = filename;
}

bool
Slice::DefinitionContext::hasMetaData() const
{
    return !_metaData.empty();
}

void
Slice::DefinitionContext::setMetaData(const StringList& metaData)
{
    _metaData = metaData;
    initSuppressedWarnings();
}

string
Slice::DefinitionContext::findMetaData(const string& prefix) const
{
    for (StringList::const_iterator p = _metaData.begin(); p != _metaData.end(); ++p)
    {
        if((*p).find(prefix) == 0)
        {
            return *p;
        }
    }

    return string();
}

StringList
Slice::DefinitionContext::getMetaData() const
{
    return _metaData;
}

bool
Slice::DefinitionContext::compatMode() const
{
    return findMetaData("3.7") == "3.7";
}

void
Slice::DefinitionContext::warning(WarningCategory category, const string& file, int line, const string& msg) const
{
    if(!suppressWarning(category))
    {
        emitWarning(file, line, msg);
    }
}

void
Slice::DefinitionContext::warning(WarningCategory category, const string& file, const string& line, const string& msg) const
{
    if(!suppressWarning(category))
    {
        emitWarning(file, line, msg);
    }
}

void
Slice::DefinitionContext::error(const string& file, int line, const string& msg) const
{
    emitError(file, line, msg);
    throw CompilerException(__FILE__, __LINE__, msg);
}

void
Slice::DefinitionContext::error(const string& file, const string& line, const string& msg) const
{
    emitError(file, line, msg);
    throw CompilerException(__FILE__, __LINE__, msg);
}

bool
Slice::DefinitionContext::suppressWarning(WarningCategory category) const
{
    return _suppressedWarnings.find(category) != _suppressedWarnings.end() ||
        _suppressedWarnings.find(All) != _suppressedWarnings.end();
}

void
Slice::DefinitionContext::initSuppressedWarnings()
{
    _suppressedWarnings.clear();
    const string prefix = "suppress-warning";
    string value = findMetaData(prefix);
    if(value == prefix)
    {
        _suppressedWarnings.insert(All);
    }
    else if(!value.empty())
    {
        assert(value.length() > prefix.length());
        if(value[prefix.length()] == ':')
        {
            value = value.substr(prefix.length() + 1);
            vector<string> result;
            IceUtilInternal::splitString(value, ",", result);
            for (vector<string>::iterator p = result.begin(); p != result.end(); ++p)
            {
                string s = IceUtilInternal::trim(*p);
                if(s == "all")
                {
                    _suppressedWarnings.insert(All);
                }
                else if(s == "deprecated")
                {
                    _suppressedWarnings.insert(Deprecated);
                }
                else if(s == "invalid-metadata")
                {
                    _suppressedWarnings.insert(InvalidMetaData);
                }
                else if(s == "reserved-identifier")
                {
                    _suppressedWarnings.insert(ReservedIdentifier);
                }
                else
                {
                    warning(InvalidMetaData, "", "", string("invalid category `") + s +
                            "' in file metadata suppress-warning");
                }
            }
        }
    }
}

// ----------------------------------------------------------------------
// Comment
// ----------------------------------------------------------------------

bool
Slice::Comment::isDeprecated() const
{
    return _isDeprecated;
}

StringList
Slice::Comment::deprecated() const
{
    return _deprecated;
}

StringList
Slice::Comment::overview() const
{
    return _overview;
}

StringList
Slice::Comment::misc() const
{
    return _misc;
}

StringList
Slice::Comment::seeAlso() const
{
    return _seeAlso;
}

StringList
Slice::Comment::returns() const
{
    return _returns;
}

map<string, StringList>
Slice::Comment::parameters() const
{
    return _parameters;
}

map<string, StringList>
Slice::Comment::exceptions() const
{
    return _exceptions;
}

Slice::Comment::Comment()
{
}

// ----------------------------------------------------------------------
// SyntaxTreeBase
// ----------------------------------------------------------------------

void
Slice::SyntaxTreeBase::destroy()
{
    _unit = 0;
}

UnitPtr
Slice::SyntaxTreeBase::unit() const
{
    return _unit;
}

DefinitionContextPtr
Slice::SyntaxTreeBase::definitionContext() const
{
    return _definitionContext;
}

void
Slice::SyntaxTreeBase::visit(ParserVisitor*, bool)
{
}

Slice::SyntaxTreeBase::SyntaxTreeBase(const UnitPtr& unt, const DefinitionContextPtr& definitionContext) :
    _unit(unt),
    _definitionContext(definitionContext)
{
    if(!_definitionContext && _unit)
    {
        _definitionContext = unt->currentDefinitionContext();
    }
}

// ----------------------------------------------------------------------
// Type
// ----------------------------------------------------------------------

Slice::Type::Type(const UnitPtr& unt) :
    SyntaxTreeBase(unt)
{
}

// ----------------------------------------------------------------------
// Builtin
// ----------------------------------------------------------------------

string
Slice::Builtin::typeId() const
{
    if (_kind == KindObject || _kind == KindValue)
    {
        return "::Ice::" + kindAsString();
    }
    else
    {
        return kindAsString();
    }
}

bool
Slice::Builtin::usesClasses() const
{
    return _kind == KindValue;
}

size_t
Slice::Builtin::minWireSize() const
{
    switch(_kind)
    {
        case KindBool: return 1;
        case KindByte: return 1;
        case KindShort: return 2;
        case KindUShort: return 2;
        case KindInt: return 4;
        case KindUInt: return 4;
        case KindVarInt: return 1;
        case KindVarUInt: return 1;
        case KindLong: return 8;
        case KindULong: return 8;
        case KindVarLong: return 1;
        case KindVarULong: return 1;
        case KindFloat: return 4;
        case KindDouble: return 8;
        case KindString: return 1; // at least one byte for an empty string.
        case KindObject: return 3; // at least a 1-character identity name (2 bytes) + empty identity category (1 byte).
        case KindValue: return 1; // at least one byte to marshal an index instead of an instance.
    }
    throw logic_error("");
}

string
Slice::Builtin::getTagFormat() const
{
    switch(_kind)
    {
        case KindBool:
        case KindByte:
            return "F1";
        case KindShort:
        case KindUShort:
            return "F2";
        case KindInt:
        case KindUInt:
        case KindFloat:
            return "F4";
        case KindLong:
        case KindULong:
        case KindDouble:
            return "F8";
        case KindVarInt:
        case KindVarUInt:
        case KindVarLong:
        case KindVarULong:
        case KindString:
            return "VSize";
        case KindValue:
            return "Class";
        case KindObject:
            return "FSize";
    }
    throw logic_error("");
}

bool
Slice::Builtin::isVariableLength() const
{
    switch(_kind)
    {
        case KindVarInt:
        case KindVarUInt:
        case KindVarLong:
        case KindVarULong:
        case KindString:
        case KindObject:
        case KindValue:
            return true;
        default:
            return false;
    }
}

bool
Slice::Builtin::isNumericType() const
{
    switch(_kind)
    {
        case KindByte:
        case KindShort:
        case KindUShort:
        case KindInt:
        case KindUInt:
        case KindVarInt:
        case KindVarUInt:
        case KindLong:
        case KindULong:
        case KindVarLong:
        case KindVarULong:
        case KindFloat:
        case KindDouble:
            return true;
        default:
            return false;
    }
}

bool
Slice::Builtin::isNumericTypeOrBool() const
{
    return isNumericType() || (_kind == KindBool);
}

bool
Slice::Builtin::isIntegralType() const
{
    switch(_kind)
    {
        case KindByte:
        case KindShort:
        case KindUShort:
        case KindInt:
        case KindUInt:
        case KindVarInt:
        case KindVarUInt:
        case KindLong:
        case KindULong:
        case KindVarLong:
        case KindVarULong:
            return true;
        default:
            return false;
    }
}

bool
Slice::Builtin::isUnsignedType() const
{
    switch(_kind)
    {
        case KindByte:
        case KindUShort:
        case KindUInt:
        case KindVarUInt:
        case KindULong:
        case KindVarULong:
            return true;
        default:
            return false;
    }
}

Slice::Builtin::Kind
Slice::Builtin::kind() const
{
    return _kind;
}

string
Slice::Builtin::kindAsString() const
{
    return builtinTable[_kind];
}

optional<Slice::Builtin::Kind>
Slice::Builtin::kindFromString(string_view str)
{
    for (size_t i = 0; i < builtinTable.size(); i++)
    {
        if(str == builtinTable[i])
        {
            return static_cast<Kind>(i);
        }
    }
    return nullopt;
}

Slice::Builtin::Builtin(const UnitPtr& ut, Kind kind) :
    SyntaxTreeBase(ut),
    Type(ut),
    _kind(kind)
{
    //
    // Builtin types do not have a definition context.
    //
    _definitionContext = 0;
}

// ----------------------------------------------------------------------
// Contained
// ----------------------------------------------------------------------

ContainerPtr
Slice::Contained::container() const
{
    return _container;
}

string
Slice::Contained::name() const
{
    return _name;
}

string
Slice::Contained::scoped() const
{
    return _scoped;
}

string
Slice::Contained::scope() const
{
    string::size_type idx = _scoped.rfind("::");
    assert(idx != string::npos);
    return string(_scoped, 0, idx + 2);
}

string
Slice::Contained::flattenedScope() const
{
    string s = scope();
    string::size_type pos = 0;
    while((pos = s.find("::", pos)) != string::npos)
    {
        s.replace(pos, 2, "_");

    }
    return s;
}

string
Slice::Contained::file() const
{
    return _file;
}

string
Slice::Contained::line() const
{
    return _line;
}

string
Slice::Contained::comment() const
{
    return _comment;
}

namespace
{

void
trimLines(StringList& l)
{
    //
    // Remove empty trailing lines.
    //
    while(!l.empty() && l.back().empty())
    {
        l.pop_back();
    }
}

StringList
splitComment(const string& c, bool stripMarkup)
{
    string comment = c;

    if(stripMarkup)
    {
        //
        // Strip HTML markup and javadoc links.
        //
        string::size_type pos = 0;
        do
        {
            pos = comment.find('<', pos);
            if(pos != string::npos)
            {
                string::size_type endpos = comment.find('>', pos);
                if(endpos == string::npos)
                {
                    break;
                }
                comment.erase(pos, endpos - pos + 1);
            }
        }
        while(pos != string::npos);

        const string link = "{@link";
        pos = 0;
        do
        {
            pos = comment.find(link, pos);
            if(pos != string::npos)
            {
                comment.erase(pos, link.size() + 1); // Erase trailing white space too.
                string::size_type endpos = comment.find('}', pos);
                if(endpos != string::npos)
                {
                    string ident = comment.substr(pos, endpos - pos);
                    comment.erase(pos, endpos - pos + 1);

                    //
                    // Replace links of the form {@link Type#member} with "Type.member".
                    //
                    string::size_type hash = ident.find('#');
                    string rest;
                    if(hash != string::npos)
                    {
                        rest = ident.substr(hash + 1);
                        ident = ident.substr(0, hash);
                        if(!ident.empty())
                        {
                            if(!rest.empty())
                            {
                                ident += "." + rest;
                            }
                        }
                        else if(!rest.empty())
                        {
                            ident = rest;
                        }
                    }

                    comment.insert(pos, ident);
                }
            }
        }
        while(pos != string::npos);
    }

    StringList result;

    string::size_type pos = 0;
    string::size_type nextPos;
    while((nextPos = comment.find_first_of('\n', pos)) != string::npos)
    {
        result.push_back(IceUtilInternal::trim(string(comment, pos, nextPos - pos)));
        pos = nextPos + 1;
    }
    string lastLine = IceUtilInternal::trim(string(comment, pos));
    if(!lastLine.empty())
    {
        result.push_back(lastLine);
    }

    trimLines(result);

    return result;
}

bool
parseCommentLine(const string& l, const string& tag, bool namedTag, string& name, string& doc)
{
    doc.clear();

    if(l.find(tag) == 0)
    {
        const string ws = " \t";

        if(namedTag)
        {
            string::size_type n = l.find_first_not_of(ws, tag.size());
            if(n == string::npos)
            {
                return false; // Malformed line, ignore it.
            }
            string::size_type end = l.find_first_of(ws, n);
            if(end == string::npos)
            {
                return false; // Malformed line, ignore it.
            }
            name = l.substr(n, end - n);
            n = l.find_first_not_of(ws, end);
            if(n != string::npos)
            {
                doc = l.substr(n);
            }
        }
        else
        {
            name.clear();

            string::size_type n = l.find_first_not_of(ws, tag.size());
            if(n == string::npos)
            {
                return false; // Malformed line, ignore it.
            }
            doc = l.substr(n);
        }

        return true;
    }

    return false;
}

}

CommentPtr
Slice::Contained::parseComment(bool stripMarkup) const
{
    CommentPtr comment = new Comment;

    comment->_isDeprecated = false;

    //
    // First check metadata for a deprecated tag.
    //
    string deprecateMetadata;
    if(findMetaData("deprecate", deprecateMetadata))
    {
        comment->_isDeprecated = true;
        if(deprecateMetadata.find("deprecate:") == 0 && deprecateMetadata.size() > 10)
        {
            comment->_deprecated.push_back(IceUtilInternal::trim(deprecateMetadata.substr(10)));
        }
    }

    if(!comment->_isDeprecated && _comment.empty())
    {
        return 0;
    }

    //
    // Split up the comment into lines.
    //
    StringList lines = splitComment(_comment, stripMarkup);

    StringList::const_iterator i;
    for (i = lines.begin(); i != lines.end(); ++i)
    {
        const string l = *i;
        if(l[0] == '@')
        {
            break;
        }
        comment->_overview.push_back(l);
    }

    enum State { StateMisc, StateParam, StateThrows, StateReturn, StateDeprecated };
    State state = StateMisc;
    string name;
    const string ws = " \t";
    const string paramTag = "@param";
    const string throwsTag = "@throws";
    const string exceptionTag = "@exception";
    const string returnTag = "@return";
    const string deprecatedTag = "@deprecated";
    const string seeTag = "@see";
    for (; i != lines.end(); ++i)
    {
        const string l = IceUtilInternal::trim(*i);
        string line;
        if(parseCommentLine(l, paramTag, true, name, line))
        {
            if(!line.empty())
            {
                state = StateParam;
                StringList sl;
                sl.push_back(line); // The first line of the description.
                comment->_parameters[name] = sl;
            }
        }
        else if(parseCommentLine(l, throwsTag, true, name, line))
        {
            if(!line.empty())
            {
                state = StateThrows;
                StringList sl;
                sl.push_back(line); // The first line of the description.
                comment->_exceptions[name] = sl;
            }
        }
        else if(parseCommentLine(l, exceptionTag, true, name, line))
        {
            if(!line.empty())
            {
                state = StateThrows;
                StringList sl;
                sl.push_back(line); // The first line of the description.
                comment->_exceptions[name] = sl;
            }
        }
        else if(parseCommentLine(l, seeTag, false, name, line))
        {
            if(!line.empty())
            {
                comment->_seeAlso.push_back(line);
            }
        }
        else if(parseCommentLine(l, returnTag, false, name, line))
        {
            if(!line.empty())
            {
                state = StateReturn;
                comment->_returns.push_back(line); // The first line of the description.
            }
        }
        else if(parseCommentLine(l, deprecatedTag, false, name, line))
        {
            comment->_isDeprecated = true;
            if(!line.empty())
            {
                state = StateDeprecated;
                comment->_deprecated.push_back(line); // The first line of the description.
            }
        }
        else if(!l.empty())
        {
            if(l[0] == '@')
            {
                //
                // Treat all other tags as miscellaneous comments.
                //
                state = StateMisc;
            }

            switch(state)
            {
                case StateMisc:
                {
                    comment->_misc.push_back(l);
                    break;
                }
                case StateParam:
                {
                    assert(!name.empty());
                    StringList sl;
                    if(comment->_parameters.find(name) != comment->_parameters.end())
                    {
                        sl = comment->_parameters[name];
                    }
                    sl.push_back(l);
                    comment->_parameters[name] = sl;
                    break;
                }
                case StateThrows:
                {
                    assert(!name.empty());
                    StringList sl;
                    if(comment->_exceptions.find(name) != comment->_exceptions.end())
                    {
                        sl = comment->_exceptions[name];
                    }
                    sl.push_back(l);
                    comment->_exceptions[name] = sl;
                    break;
                }
                case StateReturn:
                {
                    comment->_returns.push_back(l);
                    break;
                }
                case StateDeprecated:
                {
                    comment->_deprecated.push_back(l);
                    break;
                }
            }
        }
    }

    trimLines(comment->_overview);
    trimLines(comment->_deprecated);
    trimLines(comment->_misc);
    trimLines(comment->_returns);

    return comment;
}

int
Slice::Contained::includeLevel() const
{
    return _includeLevel;
}

void
Slice::Contained::updateIncludeLevel()
{
    _includeLevel = min(_includeLevel, _unit->currentIncludeLevel());
}

bool
Slice::Contained::hasMetaData(const string& meta) const
{
    return find(_metaData.begin(), _metaData.end(), meta) != _metaData.end();
}

bool
Slice::Contained::hasMetaDataWithPrefix(const string& prefix) const
{
    return !findMetaDataWithPrefix(prefix).empty();
}

bool
Slice::Contained::findMetaData(const string& prefix, string& meta) const
{
    for (list<string>::const_iterator p = _metaData.begin(); p != _metaData.end(); ++p)
    {
        if(p->find(prefix) == 0)
        {
            meta = *p;
            return true;
        }
    }

    return false;
}

string
Slice::Contained::findMetaDataWithPrefix(const string& prefix) const
{
    string meta;
    if(findMetaData(prefix, meta))
    {
        return meta.substr(prefix.size());
    }
    return "";
}

list<string>
Slice::Contained::getMetaData() const
{
    return _metaData;
}

void
Slice::Contained::setMetaData(const list<string>& metaData)
{
    _metaData = metaData;
}

//
// TODO: remove this method once "cs:" and "vb:" prefix are hard errors.
//
void
Slice::Contained::addMetaData(const string& s)
{
    _metaData.push_back(s);
}

FormatType
Slice::Contained::parseFormatMetaData(const list<string>& metaData)
{
    FormatType result = DefaultFormat; // TODO: replace FormatType here by a std::optional<FormatType>
                                       // and eliminate DefaultFormat (replaced by not-set).

    string tag;
    string prefix = "format:";
    for (list<string>::const_iterator p = metaData.begin(); p != metaData.end(); ++p)
    {
        if(p->find(prefix) == 0)
        {
            tag = *p;
            break;
        }
    }

    if(!tag.empty())
    {
        tag = tag.substr(prefix.size());
        if(tag == "compact")
        {
            result = CompactFormat;
        }
        else if(tag == "sliced")
        {
            result = SlicedFormat;
        }
        else if(tag != "default") // TODO: Allow "default" to be specified as a format value?
        {
            // TODO: How to handle invalid format?
        }
    }

    return result;
}

bool
Slice::Contained::operator<(const Contained& rhs) const
{
    return _scoped < rhs._scoped;
}

bool
Slice::Contained::operator==(const Contained& rhs) const
{
    return _scoped == rhs._scoped;
}

Slice::Contained::Contained(const ContainerPtr& container, const string& name) :
    SyntaxTreeBase(container->unit()),
    _container(container),
    _name(name)
{
    ContainedPtr cont = ContainedPtr::dynamicCast(_container);
    if(cont)
    {
        _scoped = cont->scoped();
    }
    _scoped += "::" + _name;
    assert(_unit);
    _unit->addContent(this);
    _file = _unit->currentFile();
    ostringstream s;
    s << _unit->currentLine();
    _line = s.str();
    _comment = _unit->currentComment();
    _includeLevel = _unit->currentIncludeLevel();
}

// ----------------------------------------------------------------------
// Container
// ----------------------------------------------------------------------

void
Slice::Container::destroy()
{
    for (const auto& content : _contents)
    {
        content->destroy();
    }
    _contents.clear();
    _introducedMap.clear();
    SyntaxTreeBase::destroy();
}

ModulePtr
Slice::Container::createModule(const string& name)
{
    ContainedList matches = _unit->findContents(thisScope() + name);
    matches.sort(); // Modules can occur many times...
    matches.unique(); // ... but we only want one instance of each.

    if(thisScope() == "::")
    {
        _unit->addTopLevelModule(_unit->currentFile(), name);
    }

    for (ContainedList::const_iterator p = matches.begin(); p != matches.end(); ++p)
    {
        bool differsOnlyInCase = matches.front()->name() != name;
        ModulePtr module = ModulePtr::dynamicCast(*p);
        if(module)
        {
            if(differsOnlyInCase) // Modules can be reopened only if they are capitalized correctly.
            {
                string msg = "module `" + name + "' is capitalized inconsistently with its previous name: `";
                msg += module->name() + "'";
                _unit->error(msg);
                return 0;
            }
        }
        else if(!differsOnlyInCase)
        {
            string msg = "redefinition of " + matches.front()->kindOf() + " `" + matches.front()->name();
            msg += "' as module";
            _unit->error(msg);
            return 0;
        }
        else
        {
            string msg = "module `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " name `" + matches.front()->name() + "'";
            _unit->error(msg);
            return 0;
        }
    }

    if(!checkIdentifier(name))
    {
        return 0;
    }

    ModulePtr q = new Module(this, name);
    _contents.push_back(q);
    return q;
}

ClassDefPtr
Slice::Container::createClassDef(const string& name, int id, const ClassDefPtr& base)
{
    ContainedList matches = _unit->findContents(thisScope() + name);
    for (const auto& p : matches)
    {
        ClassDeclPtr decl = ClassDeclPtr::dynamicCast(p);
        if(decl)
        {
            continue; // all good
        }

        bool differsOnlyInCase = matches.front()->name() != name;
        ClassDefPtr def = ClassDefPtr::dynamicCast(p);
        if(def)
        {
            if(differsOnlyInCase)
            {
                string msg = "class definition `" + name + "' is capitalized inconsistently with its previous name: `";
                msg += def->name() + "'";
                _unit->error(msg);
            }
            else
            {
                if(_unit->ignRedefs())
                {
                    def->updateIncludeLevel();
                    return def;
                }

                string msg = "redefinition of class `" + name + "'";
                _unit->error(msg);
            }
        }
        else if(differsOnlyInCase)
        {
            string msg = "class definition `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " name `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        else
        {
            bool declared = InterfaceDeclPtr::dynamicCast(matches.front());
            string msg = "class `" + name + "' was previously " + (declared ? "declared" : "defined")
                + " as " + prependA(matches.front()->kindOf());
            _unit->error(msg);
        }
        return 0;
    }

    // We want to run both checks, even if the first one returns false. '||' has the potential to short-circuit
    // and skip the second check, so we use '|' here instead, which will never short-circuit.
    if(!checkIdentifier(name) | !checkForGlobalDef(name, "class"))
    {
        return 0;
    }

    ClassDefPtr def = new ClassDef(this, name, id, base);
    _contents.push_back(def);

    for (const auto& q : matches)
    {
        ClassDeclPtr decl = ClassDeclPtr::dynamicCast(q);
        decl->_definition = def;
    }

    //
    // Implicitly create a class declaration for each class
    // definition. This way the code generator can rely on always
    // having a class declaration available for lookup.
    //
    ClassDeclPtr decl = createClassDecl(name);
    def->_declaration = decl;

    return def;
}

ClassDeclPtr
Slice::Container::createClassDecl(const string& name)
{
    ClassDefPtr def;

    ContainedList matches = _unit->findContents(thisScope() + name);
    for (const auto& p : matches)
    {
        ClassDefPtr clDef = ClassDefPtr::dynamicCast(p);
        if(clDef)
        {
            continue;
        }

        ClassDeclPtr clDecl = ClassDeclPtr::dynamicCast(p);
        if(clDecl)
        {
            continue;
        }

        bool differsOnlyInCase = matches.front()->name() != name;
        if(differsOnlyInCase)
        {
            string msg = "class declaration `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " name `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        else
        {
            bool declared = InterfaceDeclPtr::dynamicCast(matches.front());
            string msg = "class `" + name + "' was previously " + (declared ? "declared" : "defined")
                + " as " + prependA(matches.front()->kindOf());
            _unit->error(msg);
        }
        return 0;
    }

    // We want to run both checks, even if the first one returns false. '||' has the potential to short-circuit
    // and skip the second check, so we use '|' here instead, which will never short-circuit.
    if(!checkIdentifier(name) | !checkForGlobalDef(name, "class"))
    {
        return 0;
    }

    //
    // Multiple declarations are permissible. But if we do already
    // have a declaration for the class in this container, we don't
    // create another one.
    //
    for (const auto& q : _contents)
    {
        if(q->name() == name)
        {
            ClassDeclPtr decl = ClassDeclPtr::dynamicCast(q);
            if(decl)
            {
                return decl;
            }

            def = ClassDefPtr::dynamicCast(q);
            assert(def);
        }
    }

    _unit->currentContainer();
    ClassDeclPtr decl = new ClassDecl(this, name);
    _contents.push_back(decl);

    if(def)
    {
        decl->_definition = def;
    }

    return decl;
}

InterfaceDefPtr
Slice::Container::createInterfaceDef(const string& name, const InterfaceList& bases)
{
    ContainedList matches = _unit->findContents(thisScope() + name);
    for (const auto& p : matches)
    {
        InterfaceDeclPtr decl = InterfaceDeclPtr::dynamicCast(p);
        if(decl)
        {
            continue; // all good
        }

        bool differsOnlyInCase = matches.front()->name() != name;
        InterfaceDefPtr def = InterfaceDefPtr::dynamicCast(p);
        if (def)
        {
            if(differsOnlyInCase)
            {
                string msg = "interface definition `" + name +
                    "' is capitalized inconsistently with its previous name: `";
                msg += def->name() + "'";
                _unit->error(msg);
            }
            else
            {
                if(_unit->ignRedefs())
                {
                    def->updateIncludeLevel();
                    return def;
                }

                string msg = "redefinition of interface `" + name + "'";
                _unit->error(msg);
            }
        }
        else if (differsOnlyInCase)
        {
            string msg = "interface definition `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " name `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        else
        {
            bool declared = ClassDeclPtr::dynamicCast(matches.front());
            string msg = "interface `" + name + "' was previously " + (declared ? "declared" : "defined")
                + " as " + prependA(matches.front()->kindOf());
            _unit->error(msg);
        }
        return 0;
    }

    // We want to run both checks, even if the first one returns false. '||' has the potential to short-circuit
    // and skip the second check, so we use '|' here instead, which will never short-circuit.
    if (!checkIdentifier(name) | !checkForGlobalDef(name, "interface"))
    {
        return 0;
    }

    InterfaceDecl::checkBasesAreLegal(name, bases, _unit);

    InterfaceDefPtr def = new InterfaceDef(this, name, bases);
    _contents.push_back(def);

    for (const auto& q : matches)
    {
        InterfaceDeclPtr decl = InterfaceDeclPtr::dynamicCast(q);
        decl->_definition = def;
    }

    //
    // Implicitly create an interface declaration for each interface
    // definition. This way the code generator can rely on always
    // having an interface declaration available for lookup.
    //
    InterfaceDeclPtr decl = createInterfaceDecl(name);
    def->_declaration = decl;

    return def;
}

InterfaceDeclPtr
Slice::Container::createInterfaceDecl(const string& name)
{
    InterfaceDefPtr def;

    ContainedList matches = _unit->findContents(thisScope() + name);
    for (const auto& p : matches)
    {
        InterfaceDefPtr interfaceDef = InterfaceDefPtr::dynamicCast(p);
        if (interfaceDef)
        {
            continue;
        }

        InterfaceDeclPtr interfaceDecl = InterfaceDeclPtr::dynamicCast(p);
        if (interfaceDecl)
        {
            continue;
        }

        bool differsOnlyInCase = matches.front()->name() != name;
        if (differsOnlyInCase)
        {
            string msg = "interface declaration `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " name `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        else
        {
            bool declared = ClassDeclPtr::dynamicCast(matches.front());
            string msg = "interface `" + name + "' was previously " + (declared ? "declared" : "defined")
                + " as " + prependA(matches.front()->kindOf());
            _unit->error(msg);
        }
        return 0;
    }

    // We want to run both checks, even if the first one returns false. '||' has the potential to short-circuit
    // and skip the second check, so we use '|' here instead, which will never short-circuit.
    if (!checkIdentifier(name) | !checkForGlobalDef(name, "interface"))
    {
        return 0;
    }

    // Multiple declarations are permissible. But if we do already have a declaration for the interface in this
    // container, we don't create another one.
    for (const auto& q : _contents)
    {
        if (q->name() == name)
        {
            InterfaceDeclPtr decl = InterfaceDeclPtr::dynamicCast(q);
            if (decl)
            {
                return decl;
            }

            def = InterfaceDefPtr::dynamicCast(q);
            assert(def);
        }
    }

    _unit->currentContainer();
    InterfaceDeclPtr decl = new InterfaceDecl(this, name);
    _contents.push_back(decl);

    if (def)
    {
        decl->_definition = def;
    }

    return decl;
}

ExceptionPtr
Slice::Container::createException(const string& name, const ExceptionPtr& base, NodeType nt)
{
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        ExceptionPtr p = ExceptionPtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }
        if(matches.front()->name() == name)
        {
            string msg = "redefinition of " + matches.front()->kindOf() + " `" + matches.front()->name();
            msg += "' as exception";
            _unit->error(msg);
        }
        else
        {
            string msg = "exception `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        return 0;
    }

    checkIdentifier(name); // Don't return here -- we create the exception anyway.

    if(nt == Real)
    {
        checkForGlobalDef(name, "exception"); // Don't return here -- we create the exception anyway.
    }

    ExceptionPtr p = new Exception(this, name, base);
    _contents.push_back(p);
    return p;
}

StructPtr
Slice::Container::createStruct(const string& name, NodeType nt)
{
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        StructPtr p = StructPtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }
        if(matches.front()->name() == name)
        {
            string msg = "redefinition of " + matches.front()->kindOf() + " `" + matches.front()->name();
            msg += "' as struct";
            _unit->error(msg);
        }
        else
        {
            string msg = "struct `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        return 0;
    }

    checkIdentifier(name); // Don't return here -- we create the struct anyway.

    if(nt == Real)
    {
        checkForGlobalDef(name, "structure"); // Don't return here -- we create the struct anyway.
    }

    StructPtr p = new Struct(this, name);
    _contents.push_back(p);
    return p;
}

SequencePtr
Slice::Container::createSequence(const string& name, const TypePtr& type, const StringList& metaData,
                                 NodeType nt)
{
    _unit->checkType(type);
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        SequencePtr p = SequencePtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }
        if(matches.front()->name() == name)
        {
            string msg = "redefinition of " + matches.front()->kindOf() + " `" + matches.front()->name();
            msg += "' as sequence";
            _unit->error(msg);
        }
        else
        {
            string msg = "sequence `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        return 0;
    }

    checkIdentifier(name); // Don't return here -- we create the sequence anyway.

    if(nt == Real)
    {
        checkForGlobalDef(name, "sequence"); // Don't return here -- we create the sequence anyway.
    }

    SequencePtr p = new Sequence(this, name, type, metaData);
    _contents.push_back(p);
    return p;
}

DictionaryPtr
Slice::Container::createDictionary(const string& name, const TypePtr& keyType, const StringList& keyMetaData,
                                   const TypePtr& valueType, const StringList& valueMetaData, NodeType nt)
{
    _unit->checkType(keyType);
    _unit->checkType(valueType);
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        DictionaryPtr p = DictionaryPtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }
        if(matches.front()->name() == name)
        {
            string msg = "redefinition of " + matches.front()->kindOf() + " `" + matches.front()->name();
            msg += "' as dictionary";
            _unit->error(msg);
        }
        else
        {
            string msg = "dictionary `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        return 0;
    }

    checkIdentifier(name); // Don't return here -- we create the dictionary anyway.

    if(nt == Real)
    {
        checkForGlobalDef(name, "dictionary"); // Don't return here -- we create the dictionary anyway.
    }

    if(nt == Real)
    {
        bool containsSequence = false;
        if(!Dictionary::legalKeyType(keyType, containsSequence))
        {
            _unit->error("dictionary `" + name + "' uses an illegal key type");
            return 0;
        }
        if(containsSequence)
        {
            _unit->warning(Deprecated, "use of sequences in dictionary keys has been deprecated");
        }
    }

    DictionaryPtr p = new Dictionary(this, name, keyType, keyMetaData, valueType, valueMetaData);
    _contents.push_back(p);
    return p;
}

EnumPtr
Slice::Container::createEnum(const string& name, NodeType nt)
{
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        EnumPtr p = EnumPtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }
        if(matches.front()->name() == name)
        {
            string msg = "redefinition of " + matches.front()->kindOf() + " `" + matches.front()->name();
            msg += "' as enumeration";
            _unit->error(msg);
        }
        else
        {
            string msg = "enumeration `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        return 0;
    }

    checkIdentifier(name); // Don't return here -- we create the enumeration anyway.

    if(nt == Real)
    {
        checkForGlobalDef(name, "enumeration"); // Don't return here -- we create the enumeration anyway.
    }

    EnumPtr p = new Enum(this, name);
    _contents.push_back(p);
    return p;
}

EnumeratorPtr
Slice::Container::createEnumerator(const string& name)
{
    EnumeratorPtr p = validateEnumerator(name);
    if(!p)
    {
        p = new Enumerator(this, name);
        _contents.push_back(p);
    }
    return p;
}

EnumeratorPtr
Slice::Container::createEnumerator(const string& name, int value)
{
    EnumeratorPtr p = validateEnumerator(name);
    if(!p)
    {
        p = new Enumerator(this, name, value);
        _contents.push_back(p);
    }
    return p;
}

ConstPtr
Slice::Container::createConst(const string name, const TypePtr& constType, const StringList& metaData,
                              const SyntaxTreeBasePtr& valueType, const string& value, const string& literal,
                              NodeType nt)
{
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        ConstPtr p = ConstPtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }
        if(matches.front()->name() == name)
        {
            string msg = "redefinition of " + matches.front()->kindOf() + " `" + matches.front()->name();
            msg += "' as constant";
            _unit->error(msg);
        }
        else
        {
            string msg = "constant `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        return 0;
    }

    checkIdentifier(name); // Don't return here -- we create the constant anyway.

    if(nt == Real)
    {
        checkForGlobalDef(name, "constant"); // Don't return here -- we create the constant anyway.
    }

    SyntaxTreeBasePtr resolvedValueType = valueType;

    //
    // Validate the constant and its value; for enums, find enumerator
    //
    if(nt == Real && !validateConstant(name, constType, resolvedValueType, value, true))
    {
        return 0;
    }

    ConstPtr p = new Const(this, name, constType, metaData, resolvedValueType, value, literal);
    _contents.push_back(p);
    return p;
}

TypeList
Slice::Container::lookupType(const string& scoped, bool printError)
{
    //
    // Remove whitespace.
    //
    string sc = scoped;
    string::size_type pos;
    while((pos = sc.find_first_of(" \t\r\n")) != string::npos)
    {
        sc.erase(pos, 1);
    }

    //
    // Check for builtin type.
    //
    auto kind = Builtin::kindFromString(sc);
    if(kind)
    {
        return { _unit->builtin(kind.value()) };
    }

    //
    // Not a builtin type, try to look up a constructed type.
    //
    return lookupTypeNoBuiltin(scoped, printError);
}

TypeList
Slice::Container::lookupTypeNoBuiltin(const string& scoped, bool printError, bool ignoreUndefined)
{
    //
    // Remove whitespace.
    //
    string sc = scoped;
    string::size_type pos;
    while((pos = sc.find_first_of(" \t\r\n")) != string::npos)
    {
        sc.erase(pos, 1);
    }

    //
    // Absolute scoped name?
    //
    if(sc.size() >= 2 && sc[0] == ':')
    {
        return _unit->lookupTypeNoBuiltin(sc.substr(2), printError);
    }

    TypeList results;
    bool typeError = false;
    vector<string> errors;

    ContainedList matches = _unit->findContents(thisScope() + sc);
    for (ContainedList::const_iterator p = matches.begin(); p != matches.end(); ++p)
    {
        if (InterfaceDefPtr::dynamicCast(*p) || ClassDefPtr::dynamicCast(*p))
        {
            continue; // Ignore interface and class definitions.
        }

        if (printError && matches.front()->scoped() != (thisScope() + sc))
        {
            string msg = (*p)->kindOf() + " name `" + scoped;
            msg += "' is capitalized inconsistently with its previous name: `";
            msg += matches.front()->scoped() + "'";
            errors.push_back(msg);
        }

        ExceptionPtr ex = ExceptionPtr::dynamicCast(*p);
        if (ex)
        {
            if (printError)
            {
                string msg = "`";
                msg += sc;
                msg += "' is an exception, which cannot be used as a type";
                _unit->error(msg);
            }
            return TypeList();
        }

        TypePtr type = TypePtr::dynamicCast(*p);
        if (!type)
        {
            typeError = true;
            if (printError)
            {
                string msg = "`";
                msg += sc;
                msg += "' is not a type";
                errors.push_back(msg);
            }
            break; // Possible that correct match is higher in scope
        }
        results.push_back(type);
    }

    if(results.empty())
    {
        ContainedPtr contained = ContainedPtr::dynamicCast(this);
        if(contained)
        {
            results = contained->container()->lookupTypeNoBuiltin(sc, printError, typeError || ignoreUndefined);
        }
        else if(!typeError)
        {
            if(printError && !ignoreUndefined)
            {
                string msg = "`";
                msg += sc;
                msg += "' is not defined";
                _unit->error(msg);
            }
            return TypeList();
        }
    }

    //
    // Do not emit errors if there was a type error but a match was found in a higher scope.
    //
    if(printError && !(typeError && !results.empty()))
    {
        for (vector<string>::const_iterator p = errors.begin(); p != errors.end(); ++p)
        {
            _unit->error(*p);
        }
    }
    return results;
}

ContainedList
Slice::Container::lookupContained(const string& scoped, bool printError)
{
    //
    // Remove whitespace.
    //
    string sc = scoped;
    string::size_type pos;
    while((pos = sc.find_first_of(" \t\r\n")) != string::npos)
    {
        sc.erase(pos, 1);
    }

    //
    // Absolute scoped name?
    //
    if(sc.size() >= 2 && sc[0] == ':')
    {
        return _unit->lookupContained(sc.substr(2), printError);
    }

    ContainedList matches = _unit->findContents(thisScope() + sc);
    ContainedList results;
    for (ContainedList::const_iterator p = matches.begin(); p != matches.end(); ++p)
    {
        if(InterfaceDefPtr::dynamicCast(*p) || ClassDefPtr::dynamicCast(*p))
        {
            continue; // ignore definitions
        }

        results.push_back(*p);

        if (printError && (*p)->scoped() != (thisScope() + sc))
        {
            string msg = (*p)->kindOf() + " name `" + scoped;
            msg += "' is capitalized inconsistently with its previous name: `" + (*p)->scoped() + "'";
            _unit->error(msg);
        }
    }

    if(results.empty())
    {
        ContainedPtr contained = ContainedPtr::dynamicCast(this);
        if(!contained)
        {
            if(printError)
            {
                string msg = "`";
                msg += sc;
                msg += "' is not defined";
                _unit->error(msg);
            }
            return ContainedList();
        }
        return contained->container()->lookupContained(sc, printError);
    }
    else
    {
        return results;
    }
}

ExceptionPtr
Slice::Container::lookupException(const string& scoped, bool printError)
{
    ContainedList contained = lookupContained(scoped, printError);
    if(contained.empty())
    {
        return 0;
    }

    ExceptionList exceptions;
    for (ContainedList::iterator p = contained.begin(); p != contained.end(); ++p)
    {
        ExceptionPtr ex = ExceptionPtr::dynamicCast(*p);
        if(!ex)
        {
            if(printError)
            {
                string msg = "`";
                msg += scoped;
                msg += "' is not an exception";
                _unit->error(msg);
            }
            return 0;
        }
        exceptions.push_back(ex);
    }
    assert(exceptions.size() == 1);
    return exceptions.front();
}

UnitPtr
Slice::Container::unit() const
{
    return SyntaxTreeBase::unit();
}

ModuleList
Slice::Container::modules() const
{
    ModuleList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ModulePtr q = ModulePtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

ClassList
Slice::Container::classes() const
{
    ClassList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ClassDefPtr q = ClassDefPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

ExceptionList
Slice::Container::exceptions() const
{
    ExceptionList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ExceptionPtr q = ExceptionPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

StructList
Slice::Container::structs() const
{
    StructList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        StructPtr q = StructPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

SequenceList
Slice::Container::sequences() const
{
    SequenceList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        SequencePtr q = SequencePtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

DictionaryList
Slice::Container::dictionaries() const
{
    DictionaryList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        DictionaryPtr q = DictionaryPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

EnumList
Slice::Container::enums() const
{
    EnumList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        EnumPtr q = EnumPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

EnumeratorList
Slice::Container::enumerators() const
{
    EnumeratorList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        EnumeratorPtr q = EnumeratorPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

//
// Find enumerators using the old unscoped enumerators lookup
//
EnumeratorList
Slice::Container::enumerators(const string& scoped) const
{
    EnumeratorList result;
    string::size_type lastColon = scoped.rfind(':');

    if(lastColon == string::npos)
    {
        // check all enclosing scopes
        ContainerPtr container = const_cast<Container*>(this);
        do
        {
            EnumList enums = container->enums();
            for (EnumList::iterator p = enums.begin(); p != enums.end(); ++p)
            {
                ContainedList cl = (*p)->lookupContained(scoped, false);
                if(!cl.empty())
                {
                    result.push_back(EnumeratorPtr::dynamicCast(cl.front()));
                }
            }

            ContainedPtr contained = ContainedPtr::dynamicCast(container);
            if(contained)
            {
                container = contained->container();
            }
            else
            {
                container = 0;
            }
        }
        while(result.empty() && container);
    }
    else
    {
        // Find the referenced scope
        ContainerPtr container = const_cast<Container*>(this);
        string scope = scoped.substr(0, scoped.rfind("::"));
        ContainedList cl = container->lookupContained(scope, false);
        if(!cl.empty())
        {
            container = ContainerPtr::dynamicCast(cl.front());
            if(container)
            {
                EnumList enums = container->enums();
                string name = scoped.substr(lastColon + 1);
                for (EnumList::iterator p = enums.begin(); p != enums.end(); ++p)
                {
                    ContainedList cl2 = (*p)->lookupContained(name, false);
                    if(!cl2.empty())
                    {
                        result.push_back(EnumeratorPtr::dynamicCast(cl2.front()));
                    }
                }
            }
        }
    }

    return result;
}

ConstList
Slice::Container::consts() const
{
    ConstList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ConstPtr q = ConstPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

ContainedList
Slice::Container::contents() const
{
    return _contents;
}

bool
Slice::Container::hasSequences() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        SequencePtr s = SequencePtr::dynamicCast(*p);
        if(s)
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasSequences())
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Container::hasStructs() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        StructPtr q = StructPtr::dynamicCast(*p);
        if(q)
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasStructs())
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Container::hasExceptions() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ExceptionPtr q = ExceptionPtr::dynamicCast(*p);
        if(q)
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasExceptions())
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Container::hasClassDecls() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        if(ClassDeclPtr::dynamicCast(*p))
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasClassDecls())
        {
            return true;
        }
    }
    return false;
}

bool
Slice::Container::hasInterfaceDecls() const
{
    for (const auto& p : _contents)
    {
        if (InterfaceDeclPtr::dynamicCast(p))
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(p);
        if (container && container->hasInterfaceDecls())
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Container::hasDictionaries() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        DictionaryPtr d = DictionaryPtr::dynamicCast(*p);
        if(d)
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasDictionaries())
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Container::hasClassDefs() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        if(ClassDefPtr::dynamicCast(*p))
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasClassDefs())
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Container::hasInterfaceDefs() const
{
    for (const auto& p : _contents)
    {
        InterfaceDefPtr def = InterfaceDefPtr::dynamicCast(p);
        if (def)
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(p);
        if (container && container->hasInterfaceDefs())
        {
            return true;
        }
    }
    return false;
}

bool
Slice::Container::hasValueDefs() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ClassDefPtr cl = ClassDefPtr::dynamicCast(*p);
        if(cl)
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasValueDefs())
        {
            return true;
        }
    }
    return false;
}

bool
Slice::Container::hasOnlyClassDecls() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ModulePtr m = ModulePtr::dynamicCast(*p);
        if(m)
        {
            if(!m->hasOnlyClassDecls())
            {
                return false;
            }
        }
        else if(!ClassDeclPtr::dynamicCast(*p))
        {
            return false;
        }
    }

    return true;
}

bool
Slice::Container::hasOnlyInterfaces() const
{
    for (const auto& p : _contents)
    {
        ModulePtr m = ModulePtr::dynamicCast(p);
        if (m)
        {
            if (!m->hasOnlyInterfaces())
            {
                return false;
            }
        }
        else
        {
            InterfaceDeclPtr decl = InterfaceDeclPtr::dynamicCast(p);
            if (!decl)
            {
                InterfaceDefPtr def = InterfaceDefPtr::dynamicCast(p);
                if (!def)
                {
                    return false;
                }
            }
        }
    }
    return true;
}

bool
Slice::Container::hasOperations() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        InterfaceDefPtr def = InterfaceDefPtr::dynamicCast(*p);
        if(def && def->hasOperations())
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasOperations())
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Container::hasOtherConstructedOrExceptions() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        if(ConstructedPtr::dynamicCast(*p) && !ClassDeclPtr::dynamicCast(*p) && !ClassDefPtr::dynamicCast(*p))
        {
            return true;
        }

        if(ExceptionPtr::dynamicCast(*p))
        {
            return true;
        }

        if(ConstPtr::dynamicCast(*p))
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasOtherConstructedOrExceptions())
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Container::hasContentsWithMetaData(const string& meta) const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        if((*p)->hasMetaData(meta))
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasContentsWithMetaData(meta))
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Container::hasAsyncOps() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        InterfaceDefPtr interface = InterfaceDefPtr::dynamicCast(*p);
        if(interface)
        {
            OperationList ops = interface->operations();
            if(!ops.empty() && interface->hasMetaData("amd"))
            {
                return true;
            }
            for (OperationList::const_iterator i = ops.begin(); i != ops.end(); ++i)
            {
                OperationPtr op = *i;
                if(op->hasMetaData("amd"))
                {
                    return true;
                }
            }
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasAsyncOps())
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Container::hasContained(Contained::ContainedType type) const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        if((*p)->containedType() == type)
        {
            return true;
        }

        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container && container->hasContained(type))
        {
            return true;
        }
    }

    return false;
}

string
Slice::Container::thisScope() const
{
    string s;
    ContainedPtr contained = ContainedPtr::dynamicCast(const_cast<Container*>(this));
    if(contained)
    {
        s = contained->scoped();
    }
    s += "::";
    return s;
}

void
Slice::Container::mergeModules()
{
    for (ContainedList::iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ModulePtr mod1 = ModulePtr::dynamicCast(*p);
        if(!mod1)
        {
            continue;
        }

        DefinitionContextPtr dc1 = mod1->definitionContext();
        assert(dc1);
        StringList metaData1 = dc1->getMetaData();
        metaData1.sort();
        metaData1.unique();

        ContainedList::iterator q = p;
        ++q;
        while(q != _contents.end())
        {
            ModulePtr mod2 = ModulePtr::dynamicCast(*q);
            if(!mod2)
            {
                ++q;
                continue;
            }

            if(mod1->name() != mod2->name())
            {
                ++q;
                continue;
            }

            //
            // Compare the file metadata of the two modules being merged.
            //
            DefinitionContextPtr dc2 = mod2->definitionContext();
            assert(dc2);
            StringList metaData2 = dc2->getMetaData();
            metaData2.sort();
            metaData2.unique();
            if(!checkFileMetaData(metaData1, metaData2))
            {
                unit()->warning(All, "file metadata mismatch for module `" + mod1->name() + "' in files " +
                                dc1->filename() + " and " + dc2->filename());
            }

            mod1->_contents.splice(mod1->_contents.end(), mod2->_contents);

            if(mod1->_comment.length() < mod2->_comment.length())
            {
                mod1->_comment.swap(mod2->_comment);
            }

            mod1->_includeLevel = min(mod1->_includeLevel, mod2->_includeLevel);

            _unit->removeContent(*q);
            q = _contents.erase(q);
        }

        mod1->mergeModules();
    }
}

void
Slice::Container::sort()
{
    _contents.sort();
}

void
Slice::Container::sortContents(bool sortFields)
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ContainerPtr container = ContainerPtr::dynamicCast(*p);
        if(container)
        {
            if(!sortFields)
            {
                if(StructPtr::dynamicCast(container) ||
                   ClassDefPtr::dynamicCast(container) ||
                   InterfaceDefPtr::dynamicCast(container) ||
                   ExceptionPtr::dynamicCast(container))
                {
                    continue;
                }
            }
            //
            // Don't sort operation definitions, otherwise parameters are shown in the
            // wrong order in the synopsis.
            //
            if(!OperationPtr::dynamicCast(container))
            {
                container->sort();
            }
            container->sortContents(sortFields);
        }
    }
}

void
Slice::Container::visit(ParserVisitor* visitor, bool all)
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        if(all || (*p)->includeLevel() == 0)
        {
            (*p)->visit(visitor, all);
        }
    }
}

void
Slice::Container::containerRecDependencies(set<ConstructedPtr>& dependencies)
{
    for (ContainedList::iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ConstructedPtr constructed = ConstructedPtr::dynamicCast(*p);
        if(constructed && dependencies.find(constructed) != dependencies.end())
        {
            dependencies.insert(constructed);
            constructed->recDependencies(dependencies);
        }
    }
}

bool
Slice::Container::checkIntroduced(const string& scoped, ContainedPtr namedThing)
{
    if(scoped[0] == ':') // Only unscoped names introduce anything.
    {
        return true;
    }

    //
    // Split off first component.
    //
    string::size_type pos = scoped.find("::");
    string firstComponent = pos == string::npos ? scoped : scoped.substr(0, pos);

    //
    // If we don't have a type, the thing that is introduced is the contained for
    // the first component.
    //
    if(namedThing == 0)
    {
        ContainedList cl = lookupContained(firstComponent, false);
        if(cl.empty())
        {
            return true; // Ignore types whose creation failed previously.
        }
        namedThing = cl.front();
    }
    else
    {
        //
        // For each scope, get the container until we have the container
        // for the first scope (which is the introduced one).
        //
        ContainerPtr c;
        bool first = true;
        while(pos != string::npos)
        {
            if(first)
            {
                c = namedThing->container();
            }
            else
            {
                ContainedPtr contained = ContainedPtr::dynamicCast(c);
                if(contained)
                {
                    c = contained->container();
                }
            }
            first = false;
            if(pos != string::npos)
            {
                pos = scoped.find("::", pos + 2);
            }
        }
        if(ContainedPtr::dynamicCast(c))
        {
            namedThing = ContainedPtr::dynamicCast(c);
        }
    }

    //
    // Check if the first component is in the introduced map of this scope.
    //
    map<string, ContainedPtr, CICompare>::const_iterator it = _introducedMap.find(firstComponent);
    if(it == _introducedMap.end())
    {
        //
        // We've just introduced the first component to the current scope.
        //
        _introducedMap[firstComponent] = namedThing;    // No, insert it
    }
    else
    {
        //
        // We've previously introduced the first component to the current scope,
        // check that it has not changed meaning.
        //
        if(it->second != namedThing)
        {
            //
            // Parameter are in its own scope.
            //
            if((ParamDeclPtr::dynamicCast(it->second) && !ParamDeclPtr::dynamicCast(namedThing)) ||
               (!ParamDeclPtr::dynamicCast(it->second) && ParamDeclPtr::dynamicCast(namedThing)))
            {
                return true;
            }

            //
            // Data members are in its own scope.
            //
            if((DataMemberPtr::dynamicCast(it->second) && !DataMemberPtr::dynamicCast(namedThing)) ||
               (!DataMemberPtr::dynamicCast(it->second) && DataMemberPtr::dynamicCast(namedThing)))
            {
                return true;
            }

            _unit->error("`" + firstComponent + "' has changed meaning");
            return false;
        }
    }
    return true;
}

bool
Slice::Container::checkForGlobalDef(const string& name, const char* newConstruct)
{
    if(dynamic_cast<Unit*>(this) && strcmp(newConstruct, "module"))
    {
        _unit->error("`" + name + "': " + prependA(newConstruct) +
                     " can only be defined at module scope");
        return false;
    }
    return true;
}

Slice::Container::Container(const UnitPtr& unit) :
    SyntaxTreeBase(unit)
{
}

bool
Slice::Container::checkFileMetaData(const StringList& m1, const StringList& m2)
{
    //
    // Not all file metadata mismatches represent actual problems. We are only concerned about
    // the prefixes listed below (also see bug 2766).
    //
    static const char* prefixes[] =
    {
        "java:package",
        "python:package",
        0
    };

    //
    // Collect the metadata that is unique to each list.
    //
    StringList diffs;
    set_symmetric_difference(m1.begin(), m1.end(), m2.begin(), m2.end(), back_inserter(diffs));

    for (StringList::const_iterator p = diffs.begin(); p != diffs.end(); ++p)
    {
        for (int i = 0; prefixes[i] != 0; ++i)
        {
            if(p->find(prefixes[i]) != string::npos)
            {
                return false;
            }
        }
    }

    return true;
}

bool
Slice::Container::validateConstant(const string& name, const TypePtr& lhsType, SyntaxTreeBasePtr& valueType,
                                   const string& value, bool isConstant)
{
    TypePtr type = lhsType;

    //
    // isConstant indicates whether a constant or a data member (with a default value) is
    // being defined.
    //

    if(!type)
    {
        return false;
    }

    const string desc = isConstant ? "constant" : "data member";

    //
    // If valueType is a ConstPtr, it means the constant or data member being defined
    // refers to another constant.
    //
    const ConstPtr constant = ConstPtr::dynamicCast(valueType);

    if (!isConstant)
    {
        // The type of the data member can be T?
        if (auto optional = OptionalPtr::dynamicCast(type))
        {
            type = optional->underlying();
        }
    }

    //
    // First verify that it is legal to specify a constant or default value for the given type.
    //

    BuiltinPtr b = BuiltinPtr::dynamicCast(type);
    EnumPtr e = EnumPtr::dynamicCast(type);

    if(b)
    {
        if(b->kind() == Builtin::KindValue || b->kind() == Builtin::KindObject)
        {
            if(isConstant)
            {
                _unit->error("constant `" + name + "' has illegal type: `" + b->kindAsString() + "'");
            }
            else
            {
                _unit->error("default value not allowed for data member `" + name + "' of type `" +
                             b->kindAsString() + "'");
            }
            return false;
        }
    }
    else if(!e)
    {
        if(isConstant)
        {
            _unit->error("constant `" + name + "' has illegal type");
        }
        else
        {
            _unit->error("default value not allowed for data member `" + name + "'");
        }
        return false;
    }

    //
    // Next, verify that the type of the constant or data member is compatible with the given value.
    //

    if(b)
    {
        BuiltinPtr lt;

        if(constant)
        {
            lt = BuiltinPtr::dynamicCast(constant->type());
        }
        else
        {
            lt = BuiltinPtr::dynamicCast(valueType);
        }

        if(lt)
        {
            bool ok = false;
            if(b->kind() == lt->kind())
            {
                ok = true;
            }
            else if(b->isIntegralType())
            {
                ok = lt->isIntegralType();
            }
            else if(b->isNumericType())
            {
                ok = lt->isNumericType();
            }

            if(!ok)
            {
                string msg = "initializer of type `" + lt->kindAsString() + "' is incompatible with the type `" +
                    b->kindAsString() + "' of " + desc + " `" + name + "'";
                _unit->error(msg);
                return false;
            }
        }
        else
        {
            string msg = "type of initializer is incompatible with the type `" + b->kindAsString() + "' of " + desc +
                " `" + name + "'";
            _unit->error(msg);
            return false;
        }

        // Check that numeric values are within the type's legal range.
        int64_t min;
        uint64_t max;
        try
        {
            // `unsigned long long`s don't fit in a `long long`, so we check them separately with `stoull`.
            if(b->kind() == Builtin::KindULong)
            {
                min = 0;
                max = UINT64_MAX;
                auto val = stoull(value);
                if(val < static_cast<uint64_t>(min) || val > max)
                {
                    // `stoull` throws this if the value is outside the range of an unsigned long long.
                    // So it makes sense to piggyback on this and then handle errors all in one place.
                    throw out_of_range("");
                }
            }
            else if(b->isIntegralType())
            {
                switch(b->kind())
                {
                    case Builtin::KindByte:
                    {
                        min = 0;
                        max = UINT8_MAX;
                        break;
                    }
                    case Builtin::KindShort:
                    {
                        min = INT16_MIN;
                        max = INT16_MAX;
                        break;
                    }
                    case Builtin::KindUShort:
                    {
                        min = 0;
                        max = UINT16_MAX;
                        break;
                    }
                    case Builtin::KindInt:
                    case Builtin::KindVarInt:
                    {
                        min = INT32_MIN;
                        max = INT32_MAX;
                        break;
                    }
                    case Builtin::KindUInt:
                    case Builtin::KindVarUInt:
                    {
                        min = 0;
                        max = UINT32_MAX;
                        break;
                    }
                    case Builtin::KindLong:
                    {
                        min = INT64_MIN;
                        max = INT64_MAX;
                        break;
                    }
                    case Builtin::KindULong:
                    {
                        min = 0;
                        max = UINT64_MAX;
                        break;
                    }
                    // The first 2 bits are reserved for storing the length, so we only have 62 bits for the value.
                    case Builtin::KindVarLong:
                    {
                        // We lose another bit here for the sign.
                        min = -(1LL << 61);
                        max = (1ULL << 61) - 1;
                        break;
                    }
                    case Builtin::KindVarULong:
                    {
                        min = 0;
                        max = (1ULL << 62) - 1;
                        break;
                    }
                    default:
                    {
                        throw logic_error("");
                    }
                }

                auto val = stoll(value);
                if(val < min || val > static_cast<int64_t>(max))
                {
                    // `stoll` throws this if the value is outside the range of a long long.
                    // So it makes sense to piggyback on this and then handle errors all in one place.
                    throw out_of_range("");
                }
            }
        }
        catch(const out_of_range&)
        {
            _unit->error("initializer `" + value + "' for " + desc + " `" + name + "' out of range for type " +
                         b->kindAsString());
            return false;
        }
    }

    if(e)
    {
        if(constant)
        {
            EnumPtr ec = EnumPtr::dynamicCast(constant->type());
            if(e != ec)
            {
                string msg = "type of initializer is incompatible with the type of " + desc + " `" + name + "'";
                _unit->error(msg);
                return false;
            }
        }
        else
        {
            if(valueType)
            {
                EnumeratorPtr lte = EnumeratorPtr::dynamicCast(valueType);

                if(!lte)
                {
                    string msg = "type of initializer is incompatible with the type of " + desc + " `" + name + "'";
                    _unit->error(msg);
                    return false;
                }
                EnumeratorList elist = e->enumerators();
                if(find(elist.begin(), elist.end(), lte) == elist.end())
                {
                    string msg = "enumerator `" + value + "' is not defined in enumeration `" + e->scoped() + "'";
                    _unit->error(msg);
                    return false;
                }
            }
            else
            {
                // Check if value designates an enumerator of e
                string newVal = value;
                string::size_type lastColon = value.rfind(':');
                if(lastColon != string::npos && lastColon + 1 < value.length())
                {
                    newVal = value.substr(0, lastColon + 1) + e->name() + "::" + value.substr(lastColon + 1);
                }

                ContainedList clist = e->lookupContained(newVal, false);
                if(clist.empty())
                {
                    string msg = "`" + value + "' does not designate an enumerator of `" + e->scoped() + "'";
                    _unit->error(msg);
                    return false;
                }
                EnumeratorPtr lte = EnumeratorPtr::dynamicCast(clist.front());
                if(lte)
                {
                    valueType = lte;
                    if(lastColon != string::npos)
                    {
                        _unit->warning(Deprecated, string("referencing enumerator `") + lte->name() +
                                       "' in its enumeration's enclosing scope is deprecated");
                    }
                }
                else
                {
                    string msg = "type of initializer is incompatible with the type of " + desc + " `" + name + "'";
                    _unit->error(msg);
                    return false;
                }
            }
        }
    }

    return true;
}

EnumeratorPtr
Slice::Container::validateEnumerator(const string& name)
{
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        EnumeratorPtr p = EnumeratorPtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }
        if(matches.front()->name() == name)
        {
            _unit->error(string("redefinition of enumerator `") + name + "'");
        }
        else
        {
            string msg = "enumerator `" + name + "' differs only in capitalization from ";
            msg += "`" + matches.front()->name() + "'";
            _unit->error(msg);
        }
    }

    checkIdentifier(name); // Ignore return value.
    return 0;
}

// ----------------------------------------------------------------------
// Module
// ----------------------------------------------------------------------

Contained::ContainedType
Slice::Module::containedType() const
{
    return ContainedTypeModule;
}

bool
Slice::Module::uses(const ContainedPtr&) const
{
    return false;
}

string
Slice::Module::kindOf() const
{
    return "module";
}

void
Slice::Module::visit(ParserVisitor* visitor, bool all)
{
    if(visitor->visitModuleStart(this))
    {
        Container::visit(visitor, all);
        visitor->visitModuleEnd(this);
    }
}

Slice::Module::Module(const ContainerPtr& container, const string& name) :
    SyntaxTreeBase(container->unit()),
    Container(container->unit()),
    Contained(container, name)
{
}

bool
Slice::Module::hasOnlySubModules() const
{
    for (const auto& contained : _contents)
    {
        if (!ModulePtr::dynamicCast(contained))
        {
            return false;
        }
    }
    return true;
}

// ----------------------------------------------------------------------
// Constructed
// ----------------------------------------------------------------------

string
Slice::Constructed::typeId() const
{
    return scoped();
}

ConstructedList
Slice::Constructed::dependencies()
{
    set<ConstructedPtr> resultSet;
    recDependencies(resultSet);
    return ConstructedList(resultSet.begin(), resultSet.end());
}

Slice::Constructed::Constructed(const ContainerPtr& container, const string& name) :
    SyntaxTreeBase(container->unit()),
    Type(container->unit()),
    Contained(container, name)
{
}

// ----------------------------------------------------------------------
// ClassDecl
// ----------------------------------------------------------------------

void
Slice::ClassDecl::destroy()
{
    _definition = 0;
    SyntaxTreeBase::destroy();
}

ClassDefPtr
Slice::ClassDecl::definition() const
{
    return _definition;
}

Contained::ContainedType
Slice::ClassDecl::containedType() const
{
    return ContainedTypeClass;
}

bool
Slice::ClassDecl::uses(const ContainedPtr&) const
{
    return false;
}

bool
Slice::ClassDecl::usesClasses() const
{
    return true;
}

size_t
Slice::ClassDecl::minWireSize() const
{
    return 1; // Can be a single byte when the instance is marshaled as an index.
}

string
Slice::ClassDecl::getTagFormat() const
{
    return "Class";
}

bool
Slice::ClassDecl::isVariableLength() const
{
    return true;
}

string
Slice::ClassDecl::kindOf() const
{
    return  "class";
}

void
Slice::ClassDecl::visit(ParserVisitor* visitor, bool)
{
    visitor->visitClassDecl(this);
}

void
Slice::ClassDecl::recDependencies(set<ConstructedPtr>& dependencies)
{
    if(_definition)
    {
        _definition->containerRecDependencies(dependencies);
        ClassDefPtr base = _definition->base();
        if (base)
        {
            base->declaration()->recDependencies(dependencies);
        }
    }
}

Slice::ClassDecl::ClassDecl(const ContainerPtr& container, const string& name) :
    SyntaxTreeBase(container->unit()),
    Type(container->unit()),
    Contained(container, name),
    Constructed(container, name)
{
    _unit->currentContainer();
}

// ----------------------------------------------------------------------
// ClassDef
// ----------------------------------------------------------------------

void
Slice::ClassDef::destroy()
{
    _declaration = 0;
    _base = 0;
    Container::destroy();
}

DataMemberPtr
Slice::ClassDef::createDataMember(const string& name, const TypePtr& type, bool tagged, int tag,
                                  const SyntaxTreeBasePtr& defaultValueType, const string& defaultValue,
                                  const string& defaultLiteral)
{
    _unit->checkType(type);
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        DataMemberPtr p = DataMemberPtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }

        if(matches.front()->name() != name)
        {
            string msg = "data member `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        else
        {
            string msg = "redefinition of " + matches.front()->kindOf() + " `" + matches.front()->name();
            msg += "' as data member `" + name + "'";
            _unit->error(msg);
            return 0;
        }
    }

    //
    // Check whether any bases have defined something with the same name already.
    //
    if (_base)
    {
        for (const auto& dataMember : _base->allDataMembers())
        {
            if(dataMember->name() == name)
            {
                string msg = "data member `" + name;
                msg += "' is already defined as data member in a base class";
                _unit->error(msg);
                return 0;
            }

            string baseName = IceUtilInternal::toLower(dataMember->name());
            string newName = IceUtilInternal::toLower(name);
            if(baseName == newName)
            {
                string msg = "data member `" + name + "' differs only in capitalization from data member";
                msg += " `" + dataMember->name() + "', which is defined in a base class";
                _unit->error(msg);
            }
        }
    }

    SyntaxTreeBasePtr dlt = defaultValueType;
    string dv = defaultValue;
    string dl = defaultLiteral;

    if(dlt || (EnumPtr::dynamicCast(type) && !dv.empty()))
    {
        //
        // Validate the default value.
        //
        if(!validateConstant(name, type, dlt, dv, false))
        {
            //
            // Create the data member anyway, just without the default value.
            //
            dlt = 0;
            dv.clear();
            dl.clear();
        }
    }

    if(tagged)
    {
        //
        // Validate the tag.
        //
        DataMemberList dml = dataMembers();
        for (DataMemberList::iterator q = dml.begin(); q != dml.end(); ++q)
        {
            if((*q)->tagged() && tag == (*q)->tag())
            {
                string msg = "tag for data member `" + name + "' is already in use";
                _unit->error(msg);
                break;
            }
        }
    }

    _hasDataMembers = true;
    DataMemberPtr member = new DataMember(this, name, type, tagged, tag, dlt, dv, dl);
    _contents.push_back(member);
    return member;
}

ClassDeclPtr
Slice::ClassDef::declaration() const
{
    return _declaration;
}

ClassDefPtr
Slice::ClassDef::base() const
{
    return _base;
}

ClassList
Slice::ClassDef::allBases() const
{
    ClassList result;
    if (_base)
    {
        result.push_back(_base);
        result.merge(_base->allBases());
    }
    return result;
}

DataMemberList
Slice::ClassDef::dataMembers() const
{
    DataMemberList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        DataMemberPtr q = DataMemberPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

DataMemberList
Slice::ClassDef::sortedTaggedDataMembers() const
{
    return filterSortedTaggedDataMembers(dataMembers());
}

//
// Return the data members of this class and its parent classes, in base-to-derived order.
//
DataMemberList
Slice::ClassDef::allDataMembers() const
{
    DataMemberList result;

    //
    // Check if we have a base class. If so, recursively get the data members of the base(s).
    //
    if (_base)
    {
        result = _base->allDataMembers();
    }

    //
    // Append this class's data members.
    //
    DataMemberList myMembers = dataMembers();
    result.splice(result.end(), myMembers);

    return result;
}

DataMemberList
Slice::ClassDef::classDataMembers() const
{
    DataMemberList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        DataMemberPtr q = DataMemberPtr::dynamicCast(*p);
        if(q)
        {
            TypePtr memberType = unwrapIfOptional(q->type());
            BuiltinPtr builtin = BuiltinPtr::dynamicCast(memberType);
            if((builtin && builtin->usesClasses()) || ClassDeclPtr::dynamicCast(memberType))
            {
                result.push_back(q);
            }
        }
    }
    return result;
}

//
// Return the class data members of this class and its parent classes, in base-to-derived order.
//
DataMemberList
Slice::ClassDef::allClassDataMembers() const
{
    DataMemberList result;

    if (_base)
    {
        result = _base->allClassDataMembers();
    }

    //
    // Append this class's class members.
    //
    DataMemberList myMembers = classDataMembers();
    result.splice(result.end(), myMembers);

    return result;
}

bool
Slice::ClassDef::canBeCyclic() const
{
    if(_base && _base->canBeCyclic())
    {
        return true;
    }
    DataMemberList dml = dataMembers();
    for (DataMemberList::const_iterator i = dml.begin(); i != dml.end(); ++i)
    {
        if((*i)->type()->usesClasses())
        {
            return true;
        }
    }
    return false;
}

bool
Slice::ClassDef::isA(const string& id) const
{
    if(id == _scoped)
    {
        return true;
    }
    return _base && _base->isA(id);
}

bool
Slice::ClassDef::hasDataMembers() const
{
    return _hasDataMembers;
}

bool
Slice::ClassDef::hasDefaultValues() const
{
    DataMemberList dml = dataMembers();
    for (DataMemberList::const_iterator i = dml.begin(); i != dml.end(); ++i)
    {
        if((*i)->defaultValueType())
        {
            return true;
        }
    }

    return false;
}

bool
Slice::ClassDef::inheritsMetaData(const string& meta) const
{
    return _base && (_base->hasMetaData(meta) || _base->inheritsMetaData(meta));
}

bool
Slice::ClassDef::hasBaseDataMembers() const
{
    return _base && !_base->allDataMembers().empty();
}

Contained::ContainedType
Slice::ClassDef::containedType() const
{
    return ContainedTypeClass;
}

bool
Slice::ClassDef::uses(const ContainedPtr&) const
{
    // No uses() implementation here.
    return false;
}

string
Slice::ClassDef::kindOf() const
{
    return "class";
}

void
Slice::ClassDef::visit(ParserVisitor* visitor, bool all)
{
    if(visitor->visitClassDefStart(this))
    {
        Container::visit(visitor, all);
        visitor->visitClassDefEnd(this);
    }
}

int
Slice::ClassDef::compactId() const
{
    return _compactId;
}

StringList
Slice::ClassDef::ids() const
{
    StringList ids;
    ClassList bases = allBases();
    transform(bases.begin(), bases.end(), back_inserter(ids), [](const auto& c) { return c->scoped(); });
    StringList other;
    other.push_back(scoped());
    other.push_back("::Ice::Object");
    other.sort();
    ids.merge(other);
    ids.unique();
    return ids;
}

Slice::ClassDef::ClassDef(const ContainerPtr& container, const string& name, int id, const ClassDefPtr& base) :
    SyntaxTreeBase(container->unit()),
    Container(container->unit()),
    Contained(container, name),
    _hasDataMembers(false),
    _base(base),
    _compactId(id)
{
    if(_compactId >= 0)
    {
        _unit->addTypeId(_compactId, scoped());
    }
}

// ----------------------------------------------------------------------
// InterfaceDecl
// ----------------------------------------------------------------------

void
Slice::InterfaceDecl::destroy()
{
    _definition = 0;
    SyntaxTreeBase::destroy();
}

InterfaceDefPtr
Slice::InterfaceDecl::definition() const
{
    return _definition;
}

Contained::ContainedType
Slice::InterfaceDecl::containedType() const
{
    return ContainedTypeInterface;
}

bool
Slice::InterfaceDecl::uses(const ContainedPtr&) const
{
    return false;
}

bool
Slice::InterfaceDecl::usesClasses() const
{
    return false;
}

size_t
Slice::InterfaceDecl::minWireSize() const
{
    return 3; // See Object
}

string
Slice::InterfaceDecl::getTagFormat() const
{
    return "FSize"; // with the 1.1 encoding, the size of a proxy is encoded using a fixed-length size
}

bool
Slice::InterfaceDecl::isVariableLength() const
{
    return true;
}

string
Slice::InterfaceDecl::kindOf() const
{
    return  "interface";
}

void
Slice::InterfaceDecl::visit(ParserVisitor* visitor, bool)
{
    visitor->visitInterfaceDecl(this);
}

void
Slice::InterfaceDecl::recDependencies(set<ConstructedPtr>& dependencies)
{
    if(_definition)
    {
        _definition->containerRecDependencies(dependencies);
        InterfaceList bases = _definition->bases();
        for (InterfaceList::iterator p = bases.begin(); p != bases.end(); ++p)
        {
            (*p)->declaration()->recDependencies(dependencies);
        }
    }
}

void
Slice::InterfaceDecl::checkBasesAreLegal(const string& name, const InterfaceList& bases,
                                     const UnitPtr& ut)
{
    //
    // Check whether, for multiple inheritance, any of the bases define
    // the same operations.
    //
    if(bases.size() > 1)
    {
        //
        // We have multiple inheritance. Build a list of paths through the
        // inheritance graph, such that multiple inheritance is legal if
        // the union of the names defined in classes on each path are disjoint.
        //
        GraphPartitionList gpl;
        for (InterfaceList::const_iterator p = bases.begin(); p != bases.end(); ++p)
        {
            InterfaceList cl;
            gpl.push_back(cl);
            addPartition(gpl, gpl.rbegin(), *p);
        }

        //
        // We now have a list of partitions, with each partition containing
        // a list of class definitions. Turn the list of partitions of class
        // definitions into a list of sets of strings, with each
        // set containing the names of operations and data members defined in
        // the classes in each partition.
        //
        StringPartitionList spl = toStringPartitionList(gpl);

        //
        // Multiple inheritance is legal if no two partitions contain a common
        // name (that is, if the union of the intersections of all possible pairs
        // of partitions is empty).
        //
        checkPairIntersections(spl, name, ut);
    }
}

Slice::InterfaceDecl::InterfaceDecl(const ContainerPtr& container, const string& name) :
    SyntaxTreeBase(container->unit()),
    Type(container->unit()),
    Contained(container, name),
    Constructed(container, name)
{
    _unit->currentContainer();
}

//
// Return true if the interface definition cdp is on one of the interface lists in gpl, false otherwise.
//
bool
Slice::InterfaceDecl::isInList(const GraphPartitionList& gpl, const InterfaceDefPtr& cdp)
{
    for (GraphPartitionList::const_iterator i = gpl.begin(); i != gpl.end(); ++i)
    {
        if(find(i->begin(), i->end(), cdp) != i->end())
        {
            return true;
        }
    }
    return false;
}

void
Slice::InterfaceDecl::addPartition(GraphPartitionList& gpl,
                               GraphPartitionList::reverse_iterator tail,
                               const InterfaceDefPtr& base)
{
    //
    // If this base is on one of the partition lists already, do nothing.
    //
    if(isInList(gpl, base))
    {
        return;
    }
    //
    // Put the current base at the end of the current partition.
    //
    tail->push_back(base);
    //
    // If the base has bases in turn, recurse, adding the first base
    // of base (the left-most "grandbase") to the current partition.
    //
    if(base->bases().size())
    {
        addPartition(gpl, tail, *(base->bases().begin()));
    }
    //
    // If the base has multiple bases, each of the "grandbases"
    // except for the left-most (which we just dealt with)
    // adds a new partition.
    //
    if(base->bases().size() > 1)
    {
        InterfaceList grandBases = base->bases();
        InterfaceList::const_iterator i = grandBases.begin();
        while(++i != grandBases.end())
        {
            InterfaceList cl;
            gpl.push_back(cl);
            addPartition(gpl, gpl.rbegin(), *i);
        }
    }
}

//
// Convert the list of partitions of interface definitions into a
// list of lists, with each member list containing the operation
// names defined by the interfaces in each partition.
//
Slice::InterfaceDecl::StringPartitionList
Slice::InterfaceDecl::toStringPartitionList(const GraphPartitionList& gpl)
{
    StringPartitionList spl;
    for (GraphPartitionList::const_iterator i = gpl.begin(); i != gpl.end(); ++i)
    {
        StringList sl;
        spl.push_back(sl);
        for (InterfaceList::const_iterator j = i->begin(); j != i->end(); ++j)
        {
            OperationList operations = (*j)->operations();
            for (OperationList::const_iterator l = operations.begin(); l != operations.end(); ++l)
            {
                spl.rbegin()->push_back((*l)->name());
            }
        }
    }
    return spl;
}

//
// For all (unique) pairs of string lists, check whether an identifier in one list occurs
// in the other and, if so, complain.
//
void
Slice::InterfaceDecl::checkPairIntersections(const StringPartitionList& l, const string& name, const UnitPtr& ut)
{
    set<string> reported;
    for (StringPartitionList::const_iterator i = l.begin(); i != l.end(); ++i)
    {
        StringPartitionList::const_iterator cursor = i;
        ++cursor;
        for (StringPartitionList::const_iterator j = cursor; j != l.end(); ++j)
        {
            for (StringList::const_iterator s1 = i->begin(); s1 != i->end(); ++s1)
            {
                for (StringList::const_iterator s2 = j->begin(); s2 != j->end(); ++s2)
                {
                    if((*s1) == (*s2) && reported.find(*s1) == reported.end())
                    {
                        string msg = "ambiguous multiple inheritance: `" + name;
                        msg += "' inherits operation `" + *s1 + "' from two or more unrelated base interfaces";
                        ut->error(msg);
                        reported.insert(*s1);
                    }
                    else if(!CICompare()(*s1, *s2) && !CICompare()(*s2, *s1) &&
                            reported.find(*s1) == reported.end() && reported.find(*s2) == reported.end())
                    {
                        string msg = "ambiguous multiple inheritance: `" + name;
                        msg += "' inherits operations `" + *s1 + "' and `" + *s2;
                        msg += "', which differ only in capitalization, from unrelated base interfaces";
                        ut->error(msg);
                        reported.insert(*s1);
                        reported.insert(*s2);
                    }
                }
            }
        }
    }
}

// ----------------------------------------------------------------------
// InterfaceDef
// ----------------------------------------------------------------------

void
Slice::InterfaceDef::destroy()
{
    _declaration = 0;
    _bases.clear();
    Container::destroy();
}

OperationPtr
Slice::InterfaceDef::createOperation(const string& name,
                                 const TypePtr& returnType,
                                 bool tagged,
                                 int tag,
                                 Operation::Mode mode)
{
    _unit->checkType(returnType);
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        OperationPtr p = OperationPtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }
        if(matches.front()->name() != name)
        {
            string msg = "operation `" + name + "' differs only in capitalization from ";
            msg += matches.front()->kindOf() + " `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        string msg = "redefinition of " + matches.front()->kindOf() + " `" + matches.front()->name();
        msg += "' as operation `" + name + "'";
        _unit->error(msg);
        return 0;
    }

    //
    // Check whether enclosing interface has the same name.
    //
    if(name == this->name())
    {
        string msg = "interface name `" + name + "' cannot be used as operation name";
        _unit->error(msg);
        return 0;
    }

    string newName = IceUtilInternal::toLower(name);
    string thisName = IceUtilInternal::toLower(this->name());
    if(newName == thisName)
    {
        string msg = "operation `" + name + "' differs only in capitalization from enclosing ";
        msg += "interface name `" + this->name() + "'";
        _unit->error(msg);
        return 0;
    }

    //
    // Check whether any base has an operation with the same name already
    //
    for (const auto& baseInterface : _bases)
    {
        for (const auto& op : baseInterface->allOperations())
        {
            if(op->name() == name)
            {
                string msg = "operation `" + name + "' is already defined as an operation in a base interface";
                _unit->error(msg);
                return 0;
            }

            string baseName = IceUtilInternal::toLower(op->name());
            string newName2 = IceUtilInternal::toLower(name);
            if(baseName == newName2)
            {
                string msg = "operation `" + name + "' differs only in capitalization from operation";
                msg += " `" + op->name() + "', which is defined in a base interface";
                _unit->error(msg);
                return 0;
            }
        }
    }

    _hasOperations = true;
    OperationPtr op = new Operation(this, name, returnType, tagged, tag, mode);
    _contents.push_back(op);
    return op;
}

InterfaceDeclPtr
Slice::InterfaceDef::declaration() const
{
    return _declaration;
}

InterfaceList
Slice::InterfaceDef::bases() const
{
    return _bases;
}

InterfaceList
Slice::InterfaceDef::allBases() const
{
    InterfaceList result = _bases;
    result.sort();
    result.unique();
    for (InterfaceList::const_iterator p = _bases.begin(); p != _bases.end(); ++p)
    {
        InterfaceList li = (*p)->allBases();
        result.merge(li);
        result.unique();
    }
    return result;
}

OperationList
Slice::InterfaceDef::operations() const
{
    OperationList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        OperationPtr q = OperationPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

OperationList
Slice::InterfaceDef::allOperations() const
{
    OperationList result;
    for (InterfaceList::const_iterator p = _bases.begin(); p != _bases.end(); ++p)
    {
        OperationList li = (*p)->allOperations();
        for (OperationList::const_iterator q = li.begin(); q != li.end(); ++q)
        {
            if(find(result.begin(), result.end(), *q) == result.end())
            {
                result.push_back(*q);
            }
        }
    }

    OperationList li = operations();
    for (OperationList::const_iterator q = li.begin(); q != li.end(); ++q)
    {
        if(find(result.begin(), result.end(), *q) == result.end())
        {
            result.push_back(*q);
        }
    }
    return result;
}

bool
Slice::InterfaceDef::isA(const string& id) const
{
    if(id == _scoped)
    {
        return true;
    }
    for (InterfaceList::const_iterator p = _bases.begin(); p != _bases.end(); ++p)
    {
        if((*p)->isA(id))
        {
            return true;
        }
    }
    return false;
}

bool
Slice::InterfaceDef::hasOperations() const
{
    return _hasOperations;
}

bool
Slice::InterfaceDef::inheritsMetaData(const string& meta) const
{
    for (InterfaceList::const_iterator p = _bases.begin(); p != _bases.end(); ++p)
    {
        if((*p)->hasMetaData(meta) || (*p)->inheritsMetaData(meta))
        {
            return true;
        }
    }

    return false;
}

Contained::ContainedType
Slice::InterfaceDef::containedType() const
{
    return ContainedTypeInterface;
}

bool
Slice::InterfaceDef::uses(const ContainedPtr&) const
{
    // No uses() implementation here.
    return false;
}

string
Slice::InterfaceDef::kindOf() const
{
    return "interface";
}

void
Slice::InterfaceDef::visit(ParserVisitor* visitor, bool all)
{
    if(visitor->visitInterfaceDefStart(this))
    {
        Container::visit(visitor, all);
        visitor->visitInterfaceDefEnd(this);
    }
}

StringList
Slice::InterfaceDef::ids() const
{
    StringList ids;
    InterfaceList bases = allBases();
    transform(bases.begin(), bases.end(), back_inserter(ids), [](const auto& c) { return c->scoped(); });
    StringList other;
    other.push_back(scoped());
    other.push_back("::Ice::Object");
    other.sort();
    ids.merge(other);
    ids.unique();
    return ids;
}

Slice::InterfaceDef::InterfaceDef(const ContainerPtr& container, const string& name, const InterfaceList& bases) :
    SyntaxTreeBase(container->unit()),
    Container(container->unit()),
    Contained(container, name),
    _bases(bases),
    _hasOperations(false)
{
}

// ----------------------------------------------------------------------
// Optional
// ----------------------------------------------------------------------

Slice::Optional::Optional(const TypePtr& underlying) :
    SyntaxTreeBase(underlying->unit(), underlying->definitionContext()),
    Type(underlying->unit()),
    _underlying(underlying)
{
}

string
Slice::Optional::typeId() const
{
    return _underlying->typeId();
}

bool
Slice::Optional::usesClasses() const
{
    return _underlying->usesClasses();
}

size_t
Slice::Optional::minWireSize() const
{
    if (_underlying->isClassType())
    {
        return 1;
    }
    else if (_underlying->isInterfaceType())
    {
        return 2;
    }
    else
    {
        return 0;
    }
}

string
Slice::Optional::getTagFormat() const
{
    return _underlying->getTagFormat();
}

bool
Slice::Optional::isVariableLength() const
{
    return _underlying->isVariableLength();
}

// ----------------------------------------------------------------------
// Exception
// ----------------------------------------------------------------------

void
Slice::Exception::destroy()
{
    _base = 0;
    Container::destroy();
}

DataMemberPtr
Slice::Exception::createDataMember(const string& name, const TypePtr& type, bool tagged, int tag,
                                   const SyntaxTreeBasePtr& defaultValueType, const string& defaultValue,
                                   const string& defaultLiteral)
{
    _unit->checkType(type);
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        DataMemberPtr p = DataMemberPtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }
        if(matches.front()->name() != name)
        {
            string msg = "exception member `" + name + "' differs only in capitalization from ";
            msg += "exception member `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        else
        {
            string msg = "redefinition of exception member `" + name + "'";
            _unit->error(msg);
            return 0;
        }
    }

    //
    // Check whether any bases have defined a member with the same name already.
    //
    ExceptionList bl = allBases();
    for (ExceptionList::const_iterator q = bl.begin(); q != bl.end(); ++q)
    {
        ContainedList cl;
        DataMemberList dml = (*q)->dataMembers();
        copy(dml.begin(), dml.end(), back_inserter(cl));
        for (ContainedList::const_iterator r = cl.begin(); r != cl.end(); ++r)
        {
            if((*r)->name() == name)
            {
                string msg = "exception member `" + name + "' is already defined in a base exception";
                _unit->error(msg);
                return 0;
            }

            string baseName = IceUtilInternal::toLower((*r)->name());
            string newName = IceUtilInternal::toLower(name);
            if(baseName == newName)
            {
                string msg = "exception member `" + name + "' differs only in capitalization from exception member `";
                msg += (*r)->name() + "', which is defined in a base exception";
                _unit->error(msg);
            }
        }
    }

    SyntaxTreeBasePtr dlt = defaultValueType;
    string dv = defaultValue;
    string dl = defaultLiteral;

    if(dlt || (EnumPtr::dynamicCast(type) && !dv.empty()))
    {
        //
        // Validate the default value.
        //
        if(!validateConstant(name, type, dlt, dv, false))
        {
            //
            // Create the data member anyway, just without the default value.
            //
            dlt = 0;
            dv.clear();
            dl.clear();
        }
    }

    if(tagged)
    {
        //
        // Validate the tag.
        //
        DataMemberList dml = dataMembers();
        for (DataMemberList::iterator q = dml.begin(); q != dml.end(); ++q)
        {
            if((*q)->tagged() && tag == (*q)->tag())
            {
                string msg = "tag for data member `" + name + "' is already in use";
                _unit->error(msg);
                break;
            }
        }
    }

    DataMemberPtr p = new DataMember(this, name, type, tagged, tag, dlt, dv, dl);
    _contents.push_back(p);
    return p;
}

DataMemberList
Slice::Exception::dataMembers() const
{
    DataMemberList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        DataMemberPtr q = DataMemberPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

DataMemberList
Slice::Exception::sortedTaggedDataMembers() const
{
    return filterSortedTaggedDataMembers(dataMembers());
}

//
// Return the data members of this exception and its parent exceptions, in base-to-derived order.
//
DataMemberList
Slice::Exception::allDataMembers() const
{
    DataMemberList result;

    //
    // Check if we have a base exception. If so, recursively
    // get the data members of the base exception(s).
    //
    if(base())
    {
        result = base()->allDataMembers();
    }

    //
    // Append this exceptions's data members.
    //
    DataMemberList myMembers = dataMembers();
    result.splice(result.end(), myMembers);

    return result;
}

DataMemberList
Slice::Exception::classDataMembers() const
{
    DataMemberList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        DataMemberPtr q = DataMemberPtr::dynamicCast(*p);
        if(q)
        {
            TypePtr memberType = unwrapIfOptional(q->type());
            BuiltinPtr builtin = BuiltinPtr::dynamicCast(memberType);
            if((builtin && builtin->usesClasses()) || ClassDeclPtr::dynamicCast(memberType))
            {
                result.push_back(q);
            }
        }
    }
    return result;
}

//
// Return the class data members of this exception and its parent exceptions, in base-to-derived order.
//
DataMemberList
Slice::Exception::allClassDataMembers() const
{
    DataMemberList result;

    //
    // Check if we have a base exception. If so, recursively
    // get the class data members of the base exception(s).
    //
    if(base())
    {
        result = base()->allClassDataMembers();
    }

    //
    // Append this exceptions's class data members.
    //
    DataMemberList myMembers = classDataMembers();
    result.splice(result.end(), myMembers);

    return result;
}

ExceptionPtr
Slice::Exception::base() const
{
    return _base;
}

ExceptionList
Slice::Exception::allBases() const
{
    ExceptionList result;
    if(_base)
    {
        result = _base->allBases();
        result.push_front(_base);
    }
    return result;
}

bool
Slice::Exception::isBaseOf(const ExceptionPtr& other) const
{
    if(this->scoped() == other->scoped())
    {
        return false;
    }
    ExceptionList bases = other->allBases();
    for (ExceptionList::const_iterator i = bases.begin(); i != bases.end(); ++i)
    {
        if((*i)->scoped() == scoped())
        {
            return true;
        }
    }
    return false;
}

Contained::ContainedType
Slice::Exception::containedType() const
{
    return ContainedTypeException;
}

bool
Slice::Exception::uses(const ContainedPtr&) const
{
    // No uses() implementation here. DataMember has its own uses().
    return false;
}

bool
Slice::Exception::usesClasses(bool includeTagged) const
{
    DataMemberList dml = dataMembers();
    for (DataMemberList::const_iterator i = dml.begin(); i != dml.end(); ++i)
    {
        if((*i)->type()->usesClasses() && (includeTagged || !(*i)->tagged()))
        {
            return true;
        }
    }
    if(_base)
    {
        return _base->usesClasses(includeTagged);
    }
    return false;
}

bool
Slice::Exception::hasDefaultValues() const
{
    DataMemberList dml = dataMembers();
    for (DataMemberList::const_iterator i = dml.begin(); i != dml.end(); ++i)
    {
        if((*i)->defaultValueType())
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Exception::inheritsMetaData(const string& meta) const
{
    if(_base && (_base->hasMetaData(meta) || _base->inheritsMetaData(meta)))
    {
        return true;
    }

    return false;
}

bool
Slice::Exception::hasBaseDataMembers() const
{
    return _base && !_base->allDataMembers().empty();
}

string
Slice::Exception::kindOf() const
{
    return "exception";
}

void
Slice::Exception::visit(ParserVisitor* visitor, bool all)
{
    if(visitor->visitExceptionStart(this))
    {
        Container::visit(visitor, all);
        visitor->visitExceptionEnd(this);
    }
}

Slice::Exception::Exception(const ContainerPtr& container, const string& name, const ExceptionPtr& base) :
    SyntaxTreeBase(container->unit()),
    Container(container->unit()),
    Contained(container, name),
    _base(base)
{
}

// ----------------------------------------------------------------------
// Struct
// ----------------------------------------------------------------------

DataMemberPtr
Slice::Struct::createDataMember(const string& name, const TypePtr& type, bool tagged, int tag,
                                const SyntaxTreeBasePtr& defaultValueType, const string& defaultValue,
                                const string& defaultLiteral)
{
    _unit->checkType(type);
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        DataMemberPtr p = DataMemberPtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }
        if(matches.front()->name() != name)
        {
            string msg = "member `" + name + "' differs only in capitalization from ";
            msg += "member `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        else
        {
            string msg = "redefinition of struct member `" + name + "'";
            _unit->error(msg);
            return 0;
        }
    }

    //
    // Structures cannot contain themselves.
    //
    if(type.get() == this)
    {
        string msg = "struct `";
        msg += this->name();
        msg += "' cannot contain itself";
        _unit->error(msg);
        return 0;
    }

    SyntaxTreeBasePtr dlt = defaultValueType;
    string dv = defaultValue;
    string dl = defaultLiteral;

    if(dlt || (EnumPtr::dynamicCast(type) && !dv.empty()))
    {
        //
        // Validate the default value.
        //
        if(!validateConstant(name, type, dlt, dv, false))
        {
            //
            // Create the data member anyway, just without the default value.
            //
            dlt = 0;
            dv.clear();
            dl.clear();
        }
    }

    if(tagged)
    {
        //
        // Validate the tag.
        //
        DataMemberList dml = dataMembers();
        for (DataMemberList::iterator q = dml.begin(); q != dml.end(); ++q)
        {
            if((*q)->tagged() && tag == (*q)->tag())
            {
                string msg = "tag for data member `" + name + "' is already in use";
                _unit->error(msg);
                break;
            }
        }
    }

    DataMemberPtr p = new DataMember(this, name, type, tagged, tag, dlt, dv, dl);
    _contents.push_back(p);
    return p;
}

DataMemberList
Slice::Struct::dataMembers() const
{
    DataMemberList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        DataMemberPtr q = DataMemberPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

DataMemberList
Slice::Struct::classDataMembers() const
{
    DataMemberList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        DataMemberPtr q = DataMemberPtr::dynamicCast(*p);
        if(q)
        {
            TypePtr memberType = unwrapIfOptional(q->type());
            BuiltinPtr builtin = BuiltinPtr::dynamicCast(memberType);
            if((builtin && builtin->usesClasses()) || ClassDeclPtr::dynamicCast(memberType))
            {
                result.push_back(q);
            }
        }
    }
    return result;
}

Contained::ContainedType
Slice::Struct::containedType() const
{
    return ContainedTypeStruct;
}

bool
Slice::Struct::uses(const ContainedPtr&) const
{
    return false;
}

bool
Slice::Struct::usesClasses() const
{
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        DataMemberPtr q = DataMemberPtr::dynamicCast(*p);
        if(q)
        {
            TypePtr t = q->type();
            if(t->usesClasses())
            {
                return true;
            }
        }
    }
    return false;
}

size_t
Slice::Struct::minWireSize() const
{
    //
    // At least the sum of the minimum member sizes.
    //
    size_t sz = 0;
    DataMemberList dml = dataMembers();
    for (DataMemberList::const_iterator i = dml.begin(); i != dml.end(); ++i)
    {
        sz += (*i)->type()->minWireSize();
    }
    return sz;
}

string
Slice::Struct::getTagFormat() const
{
    return isVariableLength() ? "FSize" : "VSize";
}

bool
Slice::Struct::isVariableLength() const
{
    DataMemberList dml = dataMembers();
    for (DataMemberList::const_iterator i = dml.begin(); i != dml.end(); ++i)
    {
        if((*i)->type()->isVariableLength())
        {
            return true;
        }
    }
    return false;
}

bool
Slice::Struct::hasDefaultValues() const
{
    DataMemberList dml = dataMembers();
    for (DataMemberList::const_iterator i = dml.begin(); i != dml.end(); ++i)
    {
        if((*i)->defaultValueType())
        {
            return true;
        }
    }
    return false;
}

string
Slice::Struct::kindOf() const
{
    return "struct";
}

void
Slice::Struct::visit(ParserVisitor* visitor, bool all)
{
    if(visitor->visitStructStart(this))
    {
        Container::visit(visitor, all);
        visitor->visitStructEnd(this);
    }
}

void
Slice::Struct::recDependencies(set<ConstructedPtr>& dependencies)
{
    containerRecDependencies(dependencies);
}

Slice::Struct::Struct(const ContainerPtr& container, const string& name) :
    SyntaxTreeBase(container->unit()),
    Container(container->unit()),
    Type(container->unit()),
    Contained(container, name),
    Constructed(container, name)
{
}

// ----------------------------------------------------------------------
// Sequence
// ----------------------------------------------------------------------

TypePtr
Slice::Sequence::type() const
{
    return _type;
}

StringList
Slice::Sequence::typeMetaData() const
{
    return _typeMetaData;
}

Contained::ContainedType
Slice::Sequence::containedType() const
{
    return ContainedTypeSequence;
}

bool
Slice::Sequence::uses(const ContainedPtr& contained) const
{
    ContainedPtr contained2 = ContainedPtr::dynamicCast(_type);
    if(contained2 && contained2 == contained)
    {
        return true;
    }

    return false;
}

bool
Slice::Sequence::usesClasses() const
{
    return _type->usesClasses();
}

size_t
Slice::Sequence::minWireSize() const
{
    return 1; // An empty sequence.
}

string
Slice::Sequence::getTagFormat() const
{
    return _type->isVariableLength() ? "FSize" : "VSize";
}

bool
Slice::Sequence::isVariableLength() const
{
    return true;
}

string
Slice::Sequence::kindOf() const
{
    return "sequence";
}

void
Slice::Sequence::visit(ParserVisitor* visitor, bool)
{
    visitor->visitSequence(this);
}

void
Slice::Sequence::recDependencies(set<ConstructedPtr>& dependencies)
{
    ConstructedPtr constructed = ConstructedPtr::dynamicCast(_type);
    if(constructed && dependencies.find(constructed) != dependencies.end())
    {
        dependencies.insert(constructed);
        constructed->recDependencies(dependencies);
    }
}

Slice::Sequence::Sequence(const ContainerPtr& container, const string& name, const TypePtr& type,
                          const StringList& typeMetaData) :
    SyntaxTreeBase(container->unit()),
    Type(container->unit()),
    Contained(container, name),
    Constructed(container, name),
    _type(type),
    _typeMetaData(typeMetaData)
{
}

// ----------------------------------------------------------------------
// Dictionary
// ----------------------------------------------------------------------

TypePtr
Slice::Dictionary::keyType() const
{
    return _keyType;
}

TypePtr
Slice::Dictionary::valueType() const
{
    return _valueType;
}

StringList
Slice::Dictionary::keyMetaData() const
{
    return _keyMetaData;
}

StringList
Slice::Dictionary::valueMetaData() const
{
    return _valueMetaData;
}

Contained::ContainedType
Slice::Dictionary::containedType() const
{
    return ContainedTypeDictionary;
}

bool
Slice::Dictionary::uses(const ContainedPtr& contained) const
{
    {
        ContainedPtr contained2 = ContainedPtr::dynamicCast(_keyType);
        if(contained2 && contained2 == contained)
        {
            return true;
        }
    }

    {
        ContainedPtr contained2 = ContainedPtr::dynamicCast(_valueType);
        if(contained2 && contained2 == contained)
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Dictionary::usesClasses() const
{
    return _valueType->usesClasses();
}

size_t
Slice::Dictionary::minWireSize() const
{
    return 1; // An empty dictionary.
}

string
Slice::Dictionary::getTagFormat() const
{
    return _keyType->isVariableLength() || _valueType->isVariableLength() ? "FSize" : "VSize";
}

bool
Slice::Dictionary::isVariableLength() const
{
    return true;
}

string
Slice::Dictionary::kindOf() const
{
    return "dictionary";
}

void
Slice::Dictionary::visit(ParserVisitor* visitor, bool)
{
    visitor->visitDictionary(this);
}

void
Slice::Dictionary::recDependencies(set<ConstructedPtr>& dependencies)
{
    {
        ConstructedPtr constructed = ConstructedPtr::dynamicCast(_keyType);
        if(constructed && dependencies.find(constructed) != dependencies.end())
        {
            dependencies.insert(constructed);
            constructed->recDependencies(dependencies);
        }
    }

    {
        ConstructedPtr constructed = ConstructedPtr::dynamicCast(_valueType);
        if(constructed && dependencies.find(constructed) != dependencies.end())
        {
            dependencies.insert(constructed);
            constructed->recDependencies(dependencies);
        }
    }
}

//
// Check that the key type of a dictionary is legal. Legal types are
// integral types, string, and sequences and structs containing only
// other legal key types.
//
// Note: Allowing sequences in dictionary keys has been deprecated as
//       of Ice 3.3.0.
//
bool
Slice::Dictionary::legalKeyType(const TypePtr& type, bool& containsSequence)
{
    BuiltinPtr bp = BuiltinPtr::dynamicCast(type);
    if(bp)
    {
        switch(bp->kind())
        {
            case Builtin::KindBool:
            case Builtin::KindByte:
            case Builtin::KindShort:
            case Builtin::KindUShort:
            case Builtin::KindInt:
            case Builtin::KindUInt:
            case Builtin::KindVarInt:
            case Builtin::KindVarUInt:
            case Builtin::KindLong:
            case Builtin::KindULong:
            case Builtin::KindVarLong:
            case Builtin::KindVarULong:
            case Builtin::KindString:
                return true;
            default:
                return false;
        }
    }

    EnumPtr ep = EnumPtr::dynamicCast(type);
    if(ep)
    {
        return true;
    }

    SequencePtr seqp = SequencePtr::dynamicCast(type);
    if(seqp)
    {
        containsSequence = true;
        if(legalKeyType(seqp->type(), containsSequence))
        {
            return true;
        }
    }

    StructPtr strp = StructPtr::dynamicCast(type);
    if(strp)
    {
        DataMemberList dml = strp->dataMembers();
        for (DataMemberList::const_iterator mem = dml.begin(); mem != dml.end(); ++mem)
        {
            if(!legalKeyType((*mem)->type(), containsSequence))
            {
                return false;
            }
        }
        return true;
    }

    return false;
}

Slice::Dictionary::Dictionary(const ContainerPtr& container, const string& name, const TypePtr& keyType,
                              const StringList& keyMetaData, const TypePtr& valueType,
                              const StringList& valueMetaData) :
    SyntaxTreeBase(container->unit()),
    Type(container->unit()),
    Contained(container, name),
    Constructed(container, name),
    _keyType(keyType),
    _valueType(valueType),
    _keyMetaData(keyMetaData),
    _valueMetaData(valueMetaData)
{
}

// ----------------------------------------------------------------------
// Enum
// ----------------------------------------------------------------------

void
Slice::Enum::destroy()
{
    SyntaxTreeBase::destroy();
}

bool
Slice::Enum::explicitValue() const
{
    return _explicitValue;
}

int
Slice::Enum::minValue() const
{
    return static_cast<int>(_minValue);
}

int
Slice::Enum::maxValue() const
{
    return static_cast<int>(_maxValue);
}

Contained::ContainedType
Slice::Enum::containedType() const
{
    return ContainedTypeEnum;
}

bool
Slice::Enum::uses(const ContainedPtr&) const
{
    return false;
}

bool
Slice::Enum::usesClasses() const
{
    return false;
}

size_t
Slice::Enum::minWireSize() const
{
    return 1;
}

string
Slice::Enum::getTagFormat() const
{
    return "Size";
}

bool
Slice::Enum::isVariableLength() const
{
    return true;
}

string
Slice::Enum::kindOf() const
{
    return "enumeration";
}

void
Slice::Enum::visit(ParserVisitor* visitor, bool)
{
    visitor->visitEnum(this);
}

void
Slice::Enum::recDependencies(set<ConstructedPtr>&)
{
    // An Enum does not have any dependencies.
}

Slice::Enum::Enum(const ContainerPtr& container, const string& name) :
    SyntaxTreeBase(container->unit()),
    Container(container->unit()),
    Type(container->unit()),
    Contained(container, name),
    Constructed(container, name),
    _explicitValue(false),
    _minValue(Int32Max),
    _maxValue(0),
    _lastValue(-1)
{
}

int
Slice::Enum::newEnumerator(const EnumeratorPtr& p)
{
    if(p->explicitValue())
    {
        _explicitValue = true;
        _lastValue = p->value();

        if(_lastValue < 0)
        {
            string msg = "value for enumerator `" + p->name() + "' is out of range";
            _unit->error(msg);
        }
    }
    else
    {
        if(_lastValue == Int32Max)
        {
            string msg = "value for enumerator `" + p->name() + "' is out of range";
            _unit->error(msg);
        }
        else
        {
            //
            // If the enumerator was not assigned an explicit value, we automatically assign
            // it one more than the previous enumerator.
            //
            ++_lastValue;
        }
    }

    bool checkForDuplicates = true;
    if(_lastValue > _maxValue)
    {
        _maxValue = _lastValue;
        checkForDuplicates = false;
    }
    if(_lastValue < _minValue)
    {
        _minValue = _lastValue;
        checkForDuplicates = false;
    }

    if(checkForDuplicates)
    {
        EnumeratorList enl = enumerators();
        for (EnumeratorList::iterator q = enl.begin(); q != enl.end(); ++q)
        {
            EnumeratorPtr& r = *q;
            if(r != p && r->value() == _lastValue)
            {
                _unit->error(string("enumerator `") + p->name() + "' has the same value as enumerator `" +
                             r->name() + "'");
            }
        }
    }

    return _lastValue;
}

// ----------------------------------------------------------------------
// Enumerator
// ----------------------------------------------------------------------

EnumPtr
Slice::Enumerator::type() const
{
    return EnumPtr::dynamicCast(container());
}

Contained::ContainedType
Slice::Enumerator::containedType() const
{
    return ContainedTypeEnumerator;
}

bool
Slice::Enumerator::uses(const ContainedPtr&) const
{
    return false;
}

string
Slice::Enumerator::kindOf() const
{
    return "enumerator";
}

bool
Slice::Enumerator::explicitValue() const
{
    return _explicitValue;
}

int
Slice::Enumerator::value() const
{
    return _value;
}

Slice::Enumerator::Enumerator(const ContainerPtr& container, const string& name) :
    SyntaxTreeBase(container->unit()),
    Contained(container, name),
    _explicitValue(false),
    _value(-1)
{
    _value = EnumPtr::dynamicCast(container)->newEnumerator(this);
}

Slice::Enumerator::Enumerator(const ContainerPtr& container, const string& name, int value) :
    SyntaxTreeBase(container->unit()),
    Contained(container, name),
    _explicitValue(true),
    _value(value)
{
    EnumPtr::dynamicCast(container)->newEnumerator(this);
}

// ----------------------------------------------------------------------
// Const
// ----------------------------------------------------------------------

TypePtr
Slice::Const::type() const
{
    return _type;
}

StringList
Slice::Const::typeMetaData() const
{
    return _typeMetaData;
}

SyntaxTreeBasePtr
Slice::Const::valueType() const
{
    return _valueType;
}

string
Slice::Const::value() const
{
    return _value;
}

string
Slice::Const::literal() const
{
    return _literal;
}

Contained::ContainedType
Slice::Const::containedType() const
{
    return ContainedTypeConstant;
}

bool
Slice::Const::uses(const ContainedPtr& contained) const
{
    ContainedPtr contained2 = ContainedPtr::dynamicCast(_type);
    return (contained2 && contained2 == contained);
}

string
Slice::Const::kindOf() const
{
    return "constant";
}

void
Slice::Const::visit(ParserVisitor* visitor, bool)
{
    visitor->visitConst(this);
}

Slice::Const::Const(const ContainerPtr& container, const string& name, const TypePtr& type,
                    const StringList& typeMetaData, const SyntaxTreeBasePtr& valueType, const string& value,
                    const string& literal) :
    SyntaxTreeBase(container->unit()),
    Contained(container, name),
    _type(type),
    _typeMetaData(typeMetaData),
    _valueType(valueType),
    _value(value),
    _literal(literal)
{
    if(valueType == 0)
    {
        cerr << "const " << name << " created with null valueType" << endl;
    }

}

// ----------------------------------------------------------------------
// Operation
// ----------------------------------------------------------------------

InterfaceDefPtr
Slice::Operation::interface() const
{
    return InterfaceDefPtr::dynamicCast(_container);
}

size_t
Slice::Operation::inBitSequenceLength() const
{
    size_t length = 0;
    for (const auto& p : inParameters())
    {
        if (!p->tagged())
        {
            if (auto optional = OptionalPtr::dynamicCast(p->type()))
            {
                if (!optional->underlying()->isClassType() && !optional->underlying()->isInterfaceType())
                {
                    length++;
                }
            }
        }
    }
    return length;
}

size_t
Slice::Operation::returnBitSequenceLength() const
{
    size_t length = 0;
    for (const auto& p : outParameters())
    {
        if (!p->tagged())
        {
            if (auto optional = OptionalPtr::dynamicCast(p->type()))
            {
                if (!optional->underlying()->isClassType() && !optional->underlying()->isInterfaceType())
                {
                    length++;
                }
            }
        }
    }

    if (_returnType && !_returnIsTagged)
    {
        if (auto optional = OptionalPtr::dynamicCast(_returnType))
        {
            if (!optional->underlying()->isClassType() && !optional->underlying()->isInterfaceType())
            {
                length++;
            }
        }
    }
    return length;
}

TypePtr
Slice::Operation::returnType() const
{
    return _returnType;
}

bool
Slice::Operation::returnIsTagged() const
{
    return _returnIsTagged;
}

int
Slice::Operation::returnTag() const
{
    return _returnTag;
}

Operation::Mode
Slice::Operation::mode() const
{
    return _mode;
}

Operation::Mode
Slice::Operation::sendMode() const
{
    if(_mode == Operation::Idempotent && hasMetaData("nonmutating"))
    {
        return Operation::Nonmutating;
    }
    else
    {
        return _mode;
    }
}

bool
Slice::Operation::hasMarshaledResult() const
{
    InterfaceDefPtr interface = InterfaceDefPtr::dynamicCast(container());
    assert(interface);
    if(interface->hasMetaData("marshaled-result") || hasMetaData("marshaled-result"))
    {
        if(returnType() && isMutableAfterReturnType(returnType()))
        {
            return true;
        }
        for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
        {
            ParamDeclPtr q = ParamDeclPtr::dynamicCast(*p);
            if(q->isOutParam() && isMutableAfterReturnType(q->type()))
            {
                return true;
            }
        }
    }
    return false;
}

ParamDeclPtr
Slice::Operation::createParamDecl(const string& name, const TypePtr& type, bool isOutParam, bool tagged, int tag)
{
    _unit->checkType(type);
    ContainedList matches = _unit->findContents(thisScope() + name);
    if(!matches.empty())
    {
        ParamDeclPtr p = ParamDeclPtr::dynamicCast(matches.front());
        if(p)
        {
            if(_unit->ignRedefs())
            {
                p->updateIncludeLevel();
                return p;
            }
        }
        if(matches.front()->name() != name)
        {
            string msg = "parameter `" + name + "' differs only in capitalization from ";
            msg += "parameter `" + matches.front()->name() + "'";
            _unit->error(msg);
        }
        else
        {
            string msg = "redefinition of parameter `" + name + "'";
            _unit->error(msg);
            return 0;
        }
    }

    //
    // Check that in parameters don't follow out parameters.
    //
    if(!_contents.empty())
    {
        ParamDeclPtr p = ParamDeclPtr::dynamicCast(_contents.back());
        assert(p);
        if(p->isOutParam() && !isOutParam)
        {
            _unit->error("`" + name + "': in parameters cannot follow out parameters");
        }
    }

    if(tagged)
    {
        //
        // Check for a duplicate tag.
        //
        const string msg = "tag for parameter `" + name + "' is already in use";
        if(_returnIsTagged && tag == _returnTag)
        {
            _unit->error(msg);
        }
        else
        {
            ParamDeclList params = parameters();
            for (ParamDeclList::const_iterator p = params.begin(); p != params.end(); ++p)
            {
                if((*p)->tagged() && (*p)->tag() == tag)
                {
                    _unit->error(msg);
                    break;
                }
            }
        }
    }

    ParamDeclPtr p = new ParamDecl(this, name, type, isOutParam, tagged, tag);
    _contents.push_back(p);
    return p;
}

ParamDeclList
Slice::Operation::parameters() const
{
    ParamDeclList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ParamDeclPtr q = ParamDeclPtr::dynamicCast(*p);
        if(q)
        {
            result.push_back(q);
        }
    }
    return result;
}

ParamDeclList
Slice::Operation::inParameters() const
{
    ParamDeclList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ParamDeclPtr q = ParamDeclPtr::dynamicCast(*p);
        if(q && !q->isOutParam())
        {
            result.push_back(q);
        }
    }
    return result;
}

void
Slice::Operation::inParameters(ParamDeclList& requiredParams, ParamDeclList& taggedParams) const
{
    const ParamDeclList params = inParameters();
    for (ParamDeclList::const_iterator pli = params.begin(); pli != params.end(); ++pli)
    {
        if((*pli)->tagged())
        {
            taggedParams.push_back(*pli);
        }
        else
        {
            requiredParams.push_back(*pli);
        }
    }

    sortTaggedParameters(taggedParams);
}

ParamDeclList
Slice::Operation::outParameters() const
{
    ParamDeclList result;
    for (ContainedList::const_iterator p = _contents.begin(); p != _contents.end(); ++p)
    {
        ParamDeclPtr q = ParamDeclPtr::dynamicCast(*p);
        if(q && q->isOutParam())
        {
            result.push_back(q);
        }
    }
    return result;
}

void
Slice::Operation::outParameters(ParamDeclList& requiredParams, ParamDeclList& taggedParams) const
{
    const ParamDeclList params = outParameters();
    for (ParamDeclList::const_iterator pli = params.begin(); pli != params.end(); ++pli)
    {
        if((*pli)->tagged())
        {
            taggedParams.push_back(*pli);
        }
        else
        {
            requiredParams.push_back(*pli);
        }
    }

    sortTaggedParameters(taggedParams);
}

ExceptionList
Slice::Operation::throws() const
{
    return _throws;
}

void
Slice::Operation::setExceptionList(const ExceptionList& el)
{
    _throws = el;

    //
    // Check that no exception occurs more than once in the throws clause.
    //
    ExceptionList uniqueExceptions = el;
    uniqueExceptions.sort();
    uniqueExceptions.unique();
    if(uniqueExceptions.size() != el.size())
    {
        //
        // At least one exception appears twice.
        //
        ExceptionList tmp = el;
        tmp.sort();
        ExceptionList duplicates;
        set_difference(tmp.begin(), tmp.end(),
                       uniqueExceptions.begin(), uniqueExceptions.end(),
                       back_inserter(duplicates));
        string msg = "operation `" + name() + "' has a throws clause with ";
        if(duplicates.size() == 1)
        {
            msg += "a ";
        }
        msg += "duplicate exception";
        if(duplicates.size() > 1)
        {
            msg += "s";
        }
        ExceptionList::const_iterator i = duplicates.begin();
        msg += ": `" + (*i)->name() + "'";
        for (i = ++i; i != duplicates.end(); ++i)
        {
            msg += ", `" + (*i)->name() + "'";
        }
        _unit->error(msg);
    }
}

Contained::ContainedType
Slice::Operation::containedType() const
{
    return ContainedTypeOperation;
}

bool
Slice::Operation::uses(const ContainedPtr& contained) const
{
    {
        ContainedPtr contained2 = ContainedPtr::dynamicCast(_returnType);
        if(contained2 && contained2 == contained)
        {
            return true;
        }
    }

    for (ExceptionList::const_iterator q = _throws.begin(); q != _throws.end(); ++q)
    {
        ContainedPtr contained2 = ContainedPtr::dynamicCast(*q);
        if(contained2 && contained2 == contained)
        {
            return true;
        }
    }

    return false;
}

bool
Slice::Operation::sendsClasses(bool includeTagged) const
{
    ParamDeclList pdl = parameters();
    for (ParamDeclList::const_iterator i = pdl.begin(); i != pdl.end(); ++i)
    {
        if(!(*i)->isOutParam() && (*i)->type()->usesClasses() && (includeTagged || !(*i)->tagged()))
        {
            return true;
        }
    }
    return false;
}

bool
Slice::Operation::returnsClasses(bool includeTagged) const
{
    TypePtr t = returnType();
    if(t && t->usesClasses() && (includeTagged || !_returnIsTagged))
    {
        return true;
    }
    ParamDeclList pdl = parameters();
    for (ParamDeclList::const_iterator i = pdl.begin(); i != pdl.end(); ++i)
    {
        if((*i)->isOutParam() && (*i)->type()->usesClasses() && (includeTagged || !(*i)->tagged()))
        {
            return true;
        }
    }
    return false;
}

bool
Slice::Operation::returnsData() const
{
    TypePtr t = returnType();
    if(t)
    {
        return true;
    }
    ParamDeclList pdl = parameters();
    for (ParamDeclList::const_iterator i = pdl.begin(); i != pdl.end(); ++i)
    {
        if((*i)->isOutParam())
        {
            return true;
        }
    }
    if(!throws().empty())
    {
        return true;
    }
    return false;
}

bool
Slice::Operation::returnsMultipleValues() const
{
    size_t count = outParameters().size();

    if(returnType())
    {
        ++count;
    }

    return count > 1;
}

FormatType
Slice::Operation::format() const
{
    FormatType format = parseFormatMetaData(getMetaData());
    if(format == DefaultFormat)
    {
        ContainedPtr cont = ContainedPtr::dynamicCast(container());
        assert(cont);
        format = parseFormatMetaData(cont->getMetaData());
    }
    return format;
}

string
Slice::Operation::kindOf() const
{
    return "operation";
}

void
Slice::Operation::visit(ParserVisitor* visitor, bool)
{
    visitor->visitOperation(this);
}

Slice::Operation::Operation(const ContainerPtr& container,
                            const string& name,
                            const TypePtr& returnType,
                            bool returnIsTagged,
                            int returnTag,
                            Mode mode) :
    SyntaxTreeBase(container->unit()),
    Contained(container, name),
    Container(container->unit()),
    _returnType(returnType),
    _returnIsTagged(returnIsTagged),
    _returnTag(returnTag),
    _mode(mode)
{
}

// ----------------------------------------------------------------------
// ParamDecl
// ----------------------------------------------------------------------

TypePtr
Slice::ParamDecl::type() const
{
    return _type;
}

bool
Slice::ParamDecl::isOutParam() const
{
    return _isOutParam;
}

bool
Slice::ParamDecl::tagged() const
{
    return _tagged;
}

int
Slice::ParamDecl::tag() const
{
    return _tag;
}

Contained::ContainedType
Slice::ParamDecl::containedType() const
{
    return ContainedTypeDataMember;
}

bool
Slice::ParamDecl::uses(const ContainedPtr& contained) const
{
    ContainedPtr contained2 = ContainedPtr::dynamicCast(_type);
    if(contained2 && contained2 == contained)
    {
        return true;
    }

    return false;
}

string
Slice::ParamDecl::kindOf() const
{
    return "parameter declaration";
}

void
Slice::ParamDecl::visit(ParserVisitor* visitor, bool)
{
    visitor->visitParamDecl(this);
}

Slice::ParamDecl::ParamDecl(const ContainerPtr& container, const string& name, const TypePtr& type, bool isOutParam,
                            bool tagged, int tag) :
    SyntaxTreeBase(container->unit()),
    Contained(container, name),
    _type(type),
    _isOutParam(isOutParam),
    _tagged(tagged),
    _tag(tag)
{
}

// ----------------------------------------------------------------------
// DataMember
// ----------------------------------------------------------------------

TypePtr
Slice::DataMember::type() const
{
    return _type;
}

bool
Slice::DataMember::tagged() const
{
    return _tagged;
}

int
Slice::DataMember::tag() const
{
    return _tag;
}

string
Slice::DataMember::defaultValue() const
{
    return _defaultValue;
}

string
Slice::DataMember::defaultLiteral() const
{
    return _defaultLiteral;
}

SyntaxTreeBasePtr
Slice::DataMember::defaultValueType() const
{
    return _defaultValueType;
}

Contained::ContainedType
Slice::DataMember::containedType() const
{
    return ContainedTypeDataMember;
}

bool
Slice::DataMember::uses(const ContainedPtr& contained) const
{
    ContainedPtr contained2 = ContainedPtr::dynamicCast(_type);
    if(contained2 && contained2 == contained)
    {
        return true;
    }

    return false;
}

string
Slice::DataMember::kindOf() const
{
    return "data member";
}

void
Slice::DataMember::visit(ParserVisitor* visitor, bool)
{
    visitor->visitDataMember(this);
}

Slice::DataMember::DataMember(const ContainerPtr& container, const string& name, const TypePtr& type,
                              bool tagged, int tag, const SyntaxTreeBasePtr& defaultValueType,
                              const string& defaultValue, const string& defaultLiteral) :
    SyntaxTreeBase(container->unit()),
    Contained(container, name),
    _type(type),
    _tagged(tagged),
    _tag(tag),
    _defaultValueType(defaultValueType),
    _defaultValue(defaultValue),
    _defaultLiteral(defaultLiteral)
{
}

// ----------------------------------------------------------------------
// Unit
// ----------------------------------------------------------------------

UnitPtr
Slice::Unit::createUnit(bool ignRedefs, bool all, const StringList& defaultFileMetadata)
{
    return new Unit(ignRedefs, all, defaultFileMetadata);
}

bool
Slice::Unit::ignRedefs() const
{
    return _ignRedefs;
}

bool
Slice::Unit::compatMode() const
{
    return currentDefinitionContext()->compatMode();
}

void
Slice::Unit::checkType(const TypePtr& type)
{
    if (compatMode())
    {
        if (InterfaceDeclPtr::dynamicCast(type))
        {
            error("interface by value is no longer supported: remove [[3.7]] or specify an optional proxy");
        }
    }
}

void
Slice::Unit::setComment(const string& comment)
{
    if(comment.empty() || comment[0] != '*')
    {
        return;
    }

    _currentComment = "";

    string::size_type end = 0;
    while(true)
    {
        string::size_type begin;
        if(end == 0)
        {
            //
            // Skip past the initial whitespace.
            //
            begin = comment.find_first_not_of(" \t\r\n*", end);
        }
        else
        {
            //
            // Skip more whitespace but retain blank lines.
            //
            begin = comment.find_first_not_of(" \t*", end);
        }

        if(begin == string::npos)
        {
            break;
        }

        end = comment.find('\n', begin);
        if(end != string::npos)
        {
            if(end + 1 > begin)
            {
                _currentComment += comment.substr(begin, end + 1 - begin);
            }
            ++end;
        }
        else
        {
            end = comment.find_last_not_of(" \t\r\n*");
            if(end != string::npos)
            {
                if(end + 1 > begin)
                {
                    _currentComment += comment.substr(begin, end + 1 - begin);
                }
            }
            break;
        }
    }
}

void
Slice::Unit::addToComment(const string& comment)
{
    if(!_currentComment.empty())
    {
        _currentComment += '\n';
    }
    _currentComment += comment;
}

string
Slice::Unit::currentComment()
{
    string comment = "";
    comment.swap(_currentComment);
    return comment;
}

string
Slice::Unit::currentFile() const
{
    DefinitionContextPtr dc = currentDefinitionContext();
    if(dc)
    {
        return dc->filename();
    }
    else
    {
        return string();
    }
}

string
Slice::Unit::topLevelFile() const
{
    return _topLevelFile;
}

int
Slice::Unit::currentLine() const
{
    return slice_lineno;
}

int
Slice::Unit::setCurrentFile(const std::string& currentFile, int lineNumber)
{
    enum LineType { File, Push, Pop };

    LineType type = File;

    if(lineNumber == 0)
    {
        if(_currentIncludeLevel > 0 || currentFile != _topLevelFile)
        {
            type = Push;
        }
    }
    else
    {
        DefinitionContextPtr dc = currentDefinitionContext();
        if(dc != 0 && !dc->filename().empty() && dc->filename() != currentFile)
        {
            type = Pop;
        }
    }

    switch(type)
    {
        case Push:
        {
            if(++_currentIncludeLevel == 1)
            {
                if(find(_includeFiles.begin(), _includeFiles.end(), currentFile) == _includeFiles.end())
                {
                    _includeFiles.push_back(currentFile);
                }
            }
            pushDefinitionContext();
            _currentComment = "";
            break;
        }
        case Pop:
        {
            --_currentIncludeLevel;
            popDefinitionContext();
            _currentComment = "";
            break;
        }
        default:
        {
            break; // Do nothing
        }
    }
    if(!currentFile.empty())
    {
        DefinitionContextPtr dc = currentDefinitionContext();
        assert(dc);
        dc->setFilename(currentFile);
        _definitionContextMap.insert(make_pair(currentFile, dc));
    }

    return static_cast<int>(type);
}

int
Slice::Unit::currentIncludeLevel() const
{
    if(_all)
    {
        return 0;
    }
    else
    {
        return _currentIncludeLevel;
    }
}

void
Slice::Unit::addFileMetaData(const StringList& metaData)
{
    DefinitionContextPtr dc = currentDefinitionContext();
    assert(dc);
    //
    // Append the file metadata to any existing metadata (e.g., default file metadata).
    //
    StringList l = dc->getMetaData();
    copy(metaData.begin(), metaData.end(), back_inserter(l));
    dc->setMetaData(l);
}

void
Slice::Unit::error(const string& s)
{
    emitError(currentFile(), currentLine(), s);
    _errors++;
}

void
Slice::Unit::warning(WarningCategory category, const string& msg) const
{
    if(_definitionContextStack.empty())
    {
        emitWarning(currentFile(), currentLine(), msg);
    }
    else
    {
        _definitionContextStack.top()->warning(category, currentFile(), currentLine(), msg);
    }
}

ContainerPtr
Slice::Unit::currentContainer() const
{
    assert(!_containerStack.empty());
    return _containerStack.top();
}

void
Slice::Unit::pushContainer(const ContainerPtr& cont)
{
    _containerStack.push(cont);
}

void
Slice::Unit::popContainer()
{
    assert(!_containerStack.empty());
    _containerStack.pop();
}

DefinitionContextPtr
Slice::Unit::currentDefinitionContext() const
{
    DefinitionContextPtr dc;
    if(!_definitionContextStack.empty())
    {
        dc = _definitionContextStack.top();
    }
    return dc;
}

void
Slice::Unit::pushDefinitionContext()
{
    _definitionContextStack.push(new DefinitionContext(_currentIncludeLevel, _defaultFileMetaData));
}

void
Slice::Unit::popDefinitionContext()
{
    assert(!_definitionContextStack.empty());
    _definitionContextStack.pop();
}

DefinitionContextPtr
Slice::Unit::findDefinitionContext(const string& file) const
{
    map<string, DefinitionContextPtr>::const_iterator p = _definitionContextMap.find(file);
    if(p != _definitionContextMap.end())
    {
        return p->second;
    }
    return 0;
}

void
Slice::Unit::addContent(const ContainedPtr& contained)
{
    string scoped = IceUtilInternal::toLower(contained->scoped());
    _contentMap[scoped].push_back(contained);
}

void
Slice::Unit::removeContent(const ContainedPtr& contained)
{
    string scoped = IceUtilInternal::toLower(contained->scoped());
    map<string, ContainedList>::iterator p = _contentMap.find(scoped);
    assert(p != _contentMap.end());
    for (ContainedList::iterator q = p->second.begin(); q != p->second.end(); ++q)
    {
        if(q->get() == contained.get())
        {
            p->second.erase(q);
            return;
        }
    }
    assert(false);
}

ContainedList
Slice::Unit::findContents(const string& scoped) const
{
    assert(!scoped.empty());
    assert(scoped[0] == ':');

    string name = IceUtilInternal::toLower(scoped);
    map<string, ContainedList>::const_iterator p = _contentMap.find(name);
    if(p != _contentMap.end())
    {
        return p->second;
    }
    else
    {
        return ContainedList();
    }
}

ClassList
Slice::Unit::findDerivedClasses(const ClassDefPtr& cl) const
{
    ClassList derived;
    for (map<string, ContainedList>::const_iterator p = _contentMap.begin(); p != _contentMap.end(); ++p)
    {
        for (ContainedList::const_iterator q = p->second.begin(); q != p->second.end(); ++q)
        {
            ClassDefPtr r = ClassDefPtr::dynamicCast(*q);
            if(r && r->base() == cl)
            {
                derived.push_back(r);
            }
        }
    }
    derived.sort();
    derived.unique();
    return derived;
}

ExceptionList
Slice::Unit::findDerivedExceptions(const ExceptionPtr& ex) const
{
    ExceptionList derived;
    for (map<string, ContainedList>::const_iterator p = _contentMap.begin(); p != _contentMap.end(); ++p)
    {
        for (ContainedList::const_iterator q = p->second.begin(); q != p->second.end(); ++q)
        {
            ExceptionPtr r = ExceptionPtr::dynamicCast(*q);
            if(r)
            {
                ExceptionPtr base = r->base();
                if(base && base == ex)
                {
                    derived.push_back(r);
                }
            }
        }
    }
    derived.sort();
    derived.unique();
    return derived;
}

ContainedList
Slice::Unit::findUsedBy(const ContainedPtr& contained) const
{
    ContainedList usedBy;
    for (map<string, ContainedList>::const_iterator p = _contentMap.begin(); p != _contentMap.end(); ++p)
    {
        for (ContainedList::const_iterator q = p->second.begin(); q != p->second.end(); ++q)
        {
            if((*q)->uses(contained))
            {
                usedBy.push_back(*q);
            }
        }
    }
    usedBy.sort();
    usedBy.unique();
    return usedBy;
}

void
Slice::Unit::addTypeId(int compactId, const std::string& typeId)
{
    _typeIds.insert(make_pair(compactId, typeId));
}

std::string
Slice::Unit::getTypeId(int compactId) const
{
    map<int, string>::const_iterator p = _typeIds.find(compactId);
    if(p != _typeIds.end())
    {
        return p->second;
    }
    return string();
}

bool
Slice::Unit::hasCompactTypeId() const
{
    return _typeIds.size() > 0;
}

bool
Slice::Unit::usesConsts() const
{
    for (map<string, ContainedList>::const_iterator p = _contentMap.begin(); p != _contentMap.end(); ++p)
    {
        for (ContainedList::const_iterator q = p->second.begin(); q != p->second.end(); ++q)
        {
            ConstPtr cd = ConstPtr::dynamicCast(*q);
            if(cd)
            {
                return true;
            }
        }
    }

    return false;
}

StringList
Slice::Unit::includeFiles() const
{
    return _includeFiles;
}

StringList
Slice::Unit::allFiles() const
{
    StringList result;
    for (map<string, DefinitionContextPtr>::const_iterator p = _definitionContextMap.begin();
        p != _definitionContextMap.end(); ++p)
    {
        result.push_back(p->first);
    }
    return result;
}

int
Slice::Unit::parse(const string& filename, FILE* file, bool debug)
{
    slice_debug = debug ? 1 : 0;
    slice__flex_debug = debug ? 1 : 0;

    assert(!Slice::unit);
    Slice::unit = this;

    _currentComment = "";
    _currentIncludeLevel = 0;
    _topLevelFile = fullPath(filename);
    pushContainer(this);
    pushDefinitionContext();
    setCurrentFile(_topLevelFile, 0);

    slice_in = file;
    int status = slice_parse();
    if(_errors)
    {
        status = EXIT_FAILURE;
    }

    if(status == EXIT_FAILURE)
    {
        while(!_containerStack.empty())
        {
            popContainer();
        }
        while(!_definitionContextStack.empty())
        {
            popDefinitionContext();
        }
    }
    else
    {
        assert(_containerStack.size() == 1);
        popContainer();
        assert(_definitionContextStack.size() == 1);
        popDefinitionContext();
    }

    Slice::unit = 0;
    return status;
}

void
Slice::Unit::destroy()
{
    _contentMap.clear();
    _builtins.clear();
    Container::destroy();
}

void
Slice::Unit::visit(ParserVisitor* visitor, bool all)
{
    if(visitor->visitUnitStart(this))
    {
        Container::visit(visitor, all);
        visitor->visitUnitEnd(this);
    }
}

BuiltinPtr
Slice::Unit::builtin(Builtin::Kind kind)
{
    auto p = _builtins.find(kind);
    if(p != _builtins.end())
    {
        return p->second;
    }
    BuiltinPtr builtin = new Builtin(this, kind);
    _builtins.insert(make_pair(kind, builtin));
    return builtin;
}

OptionalPtr
Slice::Unit::optionalBuiltin(Builtin::Kind kind)
{
    auto p = _optionalBuiltins.find(kind);
    if(p != _optionalBuiltins.end())
    {
        return p->second;
    }
    OptionalPtr optionalBuiltin = new Optional(builtin(kind));
    _optionalBuiltins.insert(make_pair(kind, optionalBuiltin));
    return optionalBuiltin;
}

void
Slice::Unit::addTopLevelModule(const string& file, const string& module)
{
    map<string, set<string> >::iterator i = _fileTopLevelModules.find(file);
    if(i == _fileTopLevelModules.end())
    {
        set<string> modules;
        modules.insert(module);
        _fileTopLevelModules.insert(make_pair(file, modules));
    }
    else
    {
        i->second.insert(module);
    }
}

set<string>
Slice::Unit::getTopLevelModules(const string& file) const
{
    map<string, set<string> >::const_iterator i = _fileTopLevelModules.find(file);
    if(i == _fileTopLevelModules.end())
    {
        return set<string>();
    }
    else
    {
        return i->second;
    }
}

Slice::Unit::Unit(bool ignRedefs, bool all, const StringList& defaultFileMetadata) :
    SyntaxTreeBase(0),
    Container(0),
    _ignRedefs(ignRedefs),
    _all(all),
    _defaultFileMetaData(defaultFileMetadata),
    _errors(0),
    _currentIncludeLevel(0)

{
    _unit = this;
}

void
Slice::Unit::eraseWhiteSpace(string& s)
{
    string::size_type idx = s.find_first_not_of(" \t\r");
    if(idx != string::npos)
    {
        s.erase(0, idx);
    }
    idx = s.find_last_not_of(" \t\r");
    if(idx != string::npos)
    {
        s.erase(++idx);
    }
}

// ----------------------------------------------------------------------
// CICompare
// ----------------------------------------------------------------------

bool
Slice::CICompare::operator()(const string& s1, const string& s2) const
{
    string::const_iterator p1 = s1.begin();
    string::const_iterator p2 = s2.begin();
    while(p1 != s1.end() && p2 != s2.end() &&
          ::tolower(static_cast<unsigned char>(*p1)) == ::tolower(static_cast<unsigned char>(*p2)))
    {
        ++p1;
        ++p2;
    }
    if(p1 == s1.end() && p2 == s2.end())
    {
        return false;
    }
    else if(p1 == s1.end())
    {
        return true;
    }
    else if(p2 == s2.end())
    {
        return false;
    }
    else
    {
        return ::tolower(static_cast<unsigned char>(*p1)) < ::tolower(static_cast<unsigned char>(*p2));
    }
}

// ----------------------------------------------------------------------
// DerivedToBaseCompare
// ----------------------------------------------------------------------

bool
Slice::DerivedToBaseCompare::operator()(const ExceptionPtr& e1, const ExceptionPtr& e2) const
{
    return e2->isBaseOf(e1);
}
